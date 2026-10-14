using System;
using System.Buffers;

namespace OneYaml.Parsing;

internal ref partial struct YamlReader
{
    // A place where an implicit mapping key may start. Only the keys of open flow levels and
    // the current block line are kept, oldest first, so staleness checks cost O(1) per token.
    private struct SimpleKey
    {
        public int Level;
        public int TokenNumber;
        public int Offset;
        public int Column;
        public bool Required;
        public bool TabBefore;

        // Keys of a flow mapping may span lines and have any length; others may not.
        public bool Lenient;
        public bool Dead;
    }

    // Tokens held back while a possible key is undecided. Only a flow mapping key can grow this
    // far, since other keys are cut off at 1024 characters.
    private const int MaxPendingTokens = 1 << 20;

    // An implicit key is at most 1024 characters long (YAML 1.2.2 section 7.4.2 and 8.2.2).
    private const int MaxImplicitKey = 1024;

    private int _pos;
    private int _lineStart;
    private int _flowLevel;
    private int _indent;
    private bool _started;
    private bool _ended;
    private bool _simpleKeyAllowed;
    private bool _prevJson;
    private bool _tabBefore;
    private bool _afterDocumentEnd;

    private int[] _indents;
    private int _indentCount;

    private SimpleKey[] _keys;
    private int _keyHead;
    private int _keyTail;
    private int _staleCursor;

    // For each open flow collection: 1 when it is a mapping, 0 when it is a sequence.
    private byte[] _flowKinds;

    private Token[] _queue;
    private int _qHead;
    private int _qTail;
    private int _tokensParsed;
    private int _lastTokenEnd;

    private Token _tok;
    private bool _tokValid;

    private YamlException? _deferred;
    private int _deferredFrom;
    private int[]? _comments;
    private int _commentCount;

    private static readonly SearchValues<byte> s_lineBreaks = SearchValues.Create("\n\r"u8);

    // ---- Character classes ----

    private static bool IsBlank(byte b) => b == (byte)' ' || b == (byte)'\t';

    private static bool IsBlankZ(byte b) => b == 0 || b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';

    private static bool IsFlowIndicator(byte b)
        => b == (byte)',' || b == (byte)'[' || b == (byte)']' || b == (byte)'{' || b == (byte)'}';

    // Bytes past the end read as 0, which can never occur in validated text.
    private readonly byte At(int i) => (uint)i < (uint)_src.Length ? _src[i] : (byte)0;

    // ---- Token queue ----

    private TokenKind PeekKind()
    {
        if (_tokValid)
        {
            return _tok.Kind;
        }

        FetchMoreTokens();
        ref Token head = ref _queue[_qHead];
        if (head.Kind == TokenKind.Error)
        {
            throw _deferred!;
        }

        if ((head.Pre & 1) != 0)
        {
            _tok = default;
            _tok.Kind = TokenKind.BlockMappingStart;
            _tok.Start = _tok.End = head.Start;
        }
        else if ((head.Pre & 2) != 0)
        {
            _tok = default;
            _tok.Kind = TokenKind.Key;
            _tok.Start = _tok.End = head.Start;
        }
        else
        {
            _tok = head;
        }

        _tokValid = true;
        return _tok.Kind;
    }

    private void SkipToken()
    {
        _tokValid = false;
        ref Token head = ref _queue[_qHead];
        if ((head.Pre & 1) != 0)
        {
            head.Pre &= 0xFE;
            return;
        }

        if ((head.Pre & 2) != 0)
        {
            head.Pre &= 0xFD;
            return;
        }

        _qHead++;
        _tokensParsed++;
        if (_qHead == _qTail)
        {
            _qHead = _qTail = 0;
        }
    }

    private void Enqueue(in Token t)
    {
        if (t.Kind is not (TokenKind.BlockEnd or TokenKind.BlockSequenceStart or TokenKind.BlockMappingStart or TokenKind.StreamEnd or TokenKind.Error))
        {
            _lastTokenEnd = t.End;
        }

        if (_qTail == _queue.Length)
        {
            if (_qHead >= _queue.Length / 2)
            {
                int count = _qTail - _qHead;
                Array.Copy(_queue, _qHead, _queue, 0, count);
                _qHead = 0;
                _qTail = count;
            }
            else
            {
                Token[] bigger = ArrayPool<Token>.Shared.Rent(_queue.Length * 2);
                Array.Copy(_queue, _qHead, bigger, 0, _qTail - _qHead);
                ArrayPool<Token>.Shared.Return(_queue);
                _queue = bigger;
                _qTail -= _qHead;
                _qHead = 0;
            }
        }

        _queue[_qTail++] = t;
    }

    private void Enqueue(TokenKind kind, int start, int end)
    {
        Token t = default;
        t.Kind = kind;
        t.Start = start;
        t.End = end;
        Enqueue(in t);
    }

    private void FetchMoreTokens()
    {
        while (true)
        {
            bool need = false;
            if (_qHead == _qTail)
            {
                need = true;
            }
            else
            {
                RemoveStaleKeys();
                if (_keyHead != _keyTail && _keys[_keyHead].TokenNumber == _tokensParsed)
                {
                    need = true;
                }
            }

            if (!need)
            {
                return;
            }

            if (_ended)
            {
                throw Error("The input ended unexpectedly.", _src.Length);
            }

            if (_qTail - _qHead > MaxPendingTokens)
            {
                throw LimitError("A mapping key is too long to read.", _pos);
            }

            try
            {
                FetchNextToken();
            }
            catch (YamlException ex)
            {
                // Keep the error in the queue: the tokens before it are valid, and the events
                // they make come out first.
                _deferred = ex;
                _ended = true;

                // Tokens that were waiting to learn whether they were a key are now plain tokens;
                // a parser error at or after the first of them is a result of that, not a finding.
                _deferredFrom = _keyHead < _keyTail ? _keys[_keyHead].Offset : int.MaxValue;
                _keyHead = _keyTail = _staleCursor = 0;
                Enqueue(TokenKind.Error, _pos, _pos);
            }
        }
    }

    // ---- Simple keys ----

    private int CountChars(int from, int to)
    {
        int n = 0;
        for (int i = from; i < to; i++)
        {
            if ((_src[i] & 0xC0) != 0x80)
            {
                n++;
            }
        }

        return n;
    }

    private void RemoveStaleKeys()
    {
        int i = Math.Max(_staleCursor, _keyHead);
        while (i < _keyTail)
        {
            ref SimpleKey key = ref _keys[i];
            if (key.Lenient || key.Dead)
            {
                i++;
                continue;
            }

            bool sameLine = key.Offset >= _lineStart;
            if (sameLine && (_pos - key.Offset <= MaxImplicitKey || CountChars(key.Offset, _pos) <= MaxImplicitKey))
            {
                break;
            }

            if (key.Required)
            {
                throw Error("Expected ':' after the mapping key.", _lastTokenEnd);
            }

            key.Dead = true;
            i++;
        }

        _staleCursor = i;
        while (_keyHead < _keyTail && _keys[_keyHead].Dead)
        {
            _keyHead++;
        }

        if (_keyHead == _keyTail)
        {
            _keyHead = _keyTail = 0;
            _staleCursor = 0;
        }
    }

    private void RemoveSimpleKey()
    {
        if (_keyHead != _keyTail && _keys[_keyTail - 1].Level == _flowLevel)
        {
            if (_keys[_keyTail - 1].Required && !_keys[_keyTail - 1].Dead)
            {
                throw Error("Expected ':' after the mapping key.", _lastTokenEnd);
            }

            PopKey();
        }
    }

    private void PopKey()
    {
        _keyTail--;
        if (_keyHead >= _keyTail)
        {
            _keyHead = _keyTail = 0;
            _staleCursor = 0;
        }
        else if (_staleCursor > _keyTail)
        {
            _staleCursor = _keyTail;
        }
    }

    private void SaveSimpleKey()
    {
        if (!_simpleKeyAllowed)
        {
            return;
        }

        int column = _pos - _lineStart;
        bool required = _flowLevel == 0 && _indent == column;
        RemoveSimpleKey();
        if (_keyTail == _keys.Length)
        {
            if (_keyHead > 0)
            {
                Array.Copy(_keys, _keyHead, _keys, 0, _keyTail - _keyHead);
                _keyTail -= _keyHead;
                _staleCursor = Math.Max(0, _staleCursor - _keyHead);
                _keyHead = 0;
            }

            if (_keyTail == _keys.Length)
            {
                Grow(ref _keys, _keyTail);
            }
        }

        _keys[_keyTail++] = new SimpleKey
        {
            Level = _flowLevel,
            TokenNumber = _tokensParsed + (_qTail - _qHead),
            Offset = _pos,
            Column = column,
            Required = required,
            TabBefore = _tabBefore,
            Lenient = _flowLevel > 0 && _flowKinds[_flowLevel - 1] == 1,
        };
    }

    // ---- Indentation ----

    private bool RollIndent(int column)
    {
        if (_flowLevel != 0 || _indent >= column)
        {
            return false;
        }

        if (_indentCount == _indents.Length)
        {
            Grow(ref _indents, _indentCount);
        }

        _indents[_indentCount++] = _indent;
        _indent = column;
        return true;
    }

    private void UnrollIndent(int column)
    {
        if (_flowLevel != 0)
        {
            return;
        }

        while (_indent > column)
        {
            Enqueue(TokenKind.BlockEnd, _pos, _pos);
            _indent = _indents[--_indentCount];
        }
    }

    // ---- Comments ----

    private void RecordComment(int start, int end)
    {
        if (!_recordComments)
        {
            return;
        }

        if (_comments is null)
        {
            _comments = ArrayPool<int>.Shared.Rent(64);
        }
        else if (_commentCount + 2 > _comments.Length)
        {
            Grow(ref _comments, _commentCount);
        }

        _comments[_commentCount++] = start;
        _comments[_commentCount++] = end;
    }

    // ---- Whitespace and comments between tokens ----

    private void ScanToNextToken()
    {
        ReadOnlySpan<byte> s = _src;
        int len = s.Length;
        while (true)
        {
            int p = _pos;
            if (p == _lineStart && p + 2 < len && s[p] == 0xEF && s[p + 1] == 0xBB && s[p + 2] == 0xBF)
            {
                // A byte order mark may start a document; it is not part of the content or the column.
                // Anywhere else a mark at the start of a line would begin a document without a marker.
                if (!BomAllowedAt(p + 3))
                {
                    throw Error("A byte order mark is only allowed at the start of a document.", p + 3);
                }

                p += 3;
                _pos = p;
                _lineStart = p;
            }

            bool inIndent = p == _lineStart;
            int q = p;
            while (q < len && s[q] == (byte)' ')
            {
                q++;
            }

            int spaces = q - p;
            bool tab = false;
            while (q < len && (s[q] == (byte)' ' || s[q] == (byte)'\t'))
            {
                tab |= s[q] == (byte)'\t';
                q++;
            }

            if (q >= len)
            {
                _pos = q;
                _tabBefore = false;
                return;
            }

            byte c = s[q];
            if (c == (byte)'\n' || c == (byte)'\r')
            {
                q += c == (byte)'\r' && q + 1 < len && s[q + 1] == (byte)'\n' ? 2 : 1;
                _pos = q;
                _lineStart = q;
                if (_flowLevel == 0)
                {
                    _simpleKeyAllowed = true;
                }

                continue;
            }

            if (c == (byte)'#')
            {
                if (q == p && !inIndent)
                {
                    throw Error("A comment must be separated from the text before it by whitespace.", q);
                }

                int rel = s[q..].IndexOfAny(s_lineBreaks);
                int end = rel < 0 ? len : q + rel;
                RecordComment(q, end);
                _pos = end;
                continue;
            }

            if (inIndent)
            {
                if (tab && spaces <= _indent)
                {
                    throw Error("A tab cannot be used as indentation.", p + spaces);
                }

                if (_flowLevel > 0 && spaces <= _indent)
                {
                    throw Error("A line inside a flow collection must be indented more than its parent.", q);
                }
            }

            _pos = q;
            _tabBefore = tab;
            return;
        }
    }

    // A byte order mark is allowed where a document may start: after a document end marker
    // or in front of a document start marker.
    private readonly bool BomAllowedAt(int after)
    {
        return _afterDocumentEnd || IsDocumentMarkerAt(after, (byte)'-');
    }

    // A marker at the start of a line, allowing a byte order mark in front of it.
    private readonly bool IsMarkerAtLineStart(int p)
    {
        if (At(p) == 0xEF && At(p + 1) == 0xBB && At(p + 2) == 0xBF)
        {
            p += 3;
        }

        return IsDocumentMarkerAt(p, (byte)'-') || IsDocumentMarkerAt(p, (byte)'.');
    }

    private readonly bool IsDocumentMarkerAt(int p, byte marker)
    {
        return At(p) == marker && At(p + 1) == marker && At(p + 2) == marker && IsBlankZ(At(p + 3));
    }

    // ---- Tokens ----

    private void FetchNextToken()
    {
        if (!_started)
        {
            _started = true;
            _simpleKeyAllowed = true;
            Enqueue(TokenKind.StreamStart, 0, 0);
            return;
        }

        bool prevJson = _prevJson;
        _prevJson = false;
        ScanToNextToken();
        RemoveStaleKeys();
        int column = _pos - _lineStart;
        UnrollIndent(column);
        if (_pos >= _src.Length)
        {
            FetchStreamEnd();
            return;
        }

        byte c = _src[_pos];
        if (column == 0)
        {
            if (c == (byte)'%')
            {
                FetchDirective();
                return;
            }

            if (c == (byte)'-' && IsDocumentMarkerAt(_pos, (byte)'-'))
            {
                FetchDocumentIndicator(TokenKind.DocumentStart);
                return;
            }

            if (c == (byte)'.' && IsDocumentMarkerAt(_pos, (byte)'.'))
            {
                FetchDocumentIndicator(TokenKind.DocumentEnd);
                return;
            }
        }

        _afterDocumentEnd = false;
        byte next = At(_pos + 1);
        switch (c)
        {
            case (byte)'[':
                FetchFlowStart(TokenKind.FlowSequenceStart);
                return;
            case (byte)'{':
                FetchFlowStart(TokenKind.FlowMappingStart);
                return;
            case (byte)']':
                if (_flowLevel == 0)
                {
                    throw Error("This closing bracket has no opening bracket.", _pos);
                }

                FetchFlowEnd(TokenKind.FlowSequenceEnd);
                return;
            case (byte)'}':
                if (_flowLevel == 0)
                {
                    throw Error("This closing brace has no opening brace.", _pos);
                }

                FetchFlowEnd(TokenKind.FlowMappingEnd);
                return;
            case (byte)',':
                if (_flowLevel == 0)
                {
                    throw Error("A comma is only allowed inside a flow collection.", _pos);
                }

                RemoveSimpleKey();
                _simpleKeyAllowed = true;
                Enqueue(TokenKind.FlowEntry, _pos, _pos + 1);
                _pos++;
                return;
            case (byte)'-':
                if (IsBlankZ(next))
                {
                    FetchBlockEntry(column);
                    return;
                }

                break;
            case (byte)'?':
                if (IsBlankZ(next) || (_flowLevel > 0 && IsFlowIndicator(next)))
                {
                    FetchKey(column);
                    return;
                }

                break;
            case (byte)':':
                if (IsBlankZ(next) || (_flowLevel > 0 && (IsFlowIndicator(next) || prevJson)))
                {
                    FetchValue(column);
                    return;
                }

                break;
            case (byte)'*':
                FetchAnchor(TokenKind.Alias);
                return;
            case (byte)'&':
                FetchAnchor(TokenKind.Anchor);
                return;
            case (byte)'!':
                FetchTag();
                return;
            case (byte)'|':
            case (byte)'>':
                if (_flowLevel == 0)
                {
                    FetchBlockScalar(c == (byte)'|');
                    return;
                }

                throw Error("A block scalar is not allowed inside a flow collection.", _pos);
            case (byte)'\'':
                FetchSingleQuoted();
                return;
            case (byte)'"':
                FetchDoubleQuoted();
                return;
            case (byte)'#':
                throw Error("A comment must be separated from the text before it by whitespace.", _pos);
            case (byte)'%':
            case (byte)'@':
            case (byte)'`':
                throw Error("This character cannot start a value.", _pos);
        }

        // The remaining '-', '?' and ':' are followed by a non-space and start a plain scalar,
        // except in a flow collection, where a flow indicator after them cannot belong to the text.
        if (_flowLevel > 0 && (c == (byte)'-' || c == (byte)'?' || c == (byte)':') && IsFlowIndicator(next))
        {
            throw Error("This character cannot start a value here.", _pos);
        }

        FetchPlainScalar();
    }

    private void FetchStreamEnd()
    {
        _lineStart = _pos;
        UnrollIndent(-1);
        RemoveSimpleKey();
        _simpleKeyAllowed = false;
        _ended = true;
        Enqueue(TokenKind.StreamEnd, _pos, _pos);
    }

    private void FetchDocumentIndicator(TokenKind kind)
    {
        UnrollIndent(-1);
        RemoveSimpleKey();
        _simpleKeyAllowed = false;
        int start = _pos;
        _pos += 3;
        Enqueue(kind, start, _pos);
        if (kind == TokenKind.DocumentEnd)
        {
            _afterDocumentEnd = true;
            // Only whitespace and a comment may follow the marker on its line.
            int p = _pos;
            while (IsBlank(At(p)))
            {
                p++;
            }

            byte c = At(p);
            if (c == (byte)'#' && p > _pos)
            {
                return;
            }

            if (c != 0 && c != (byte)'\n' && c != (byte)'\r')
            {
                throw Error("Nothing but a comment may follow a document end marker on its line.", p);
            }
        }
    }

    private void FetchFlowStart(TokenKind kind)
    {
        SaveSimpleKey();
        if (_flowLevel == _flowKinds.Length)
        {
            Grow(ref _flowKinds, _flowLevel);
        }

        _flowKinds[_flowLevel] = kind == TokenKind.FlowMappingStart ? (byte)1 : (byte)0;
        _flowLevel++;
        _simpleKeyAllowed = true;
        Enqueue(kind, _pos, _pos + 1);
        _pos++;
    }

    private void FetchFlowEnd(TokenKind kind)
    {
        RemoveSimpleKey();
        if (_flowLevel > 0)
        {
            _flowLevel--;
        }

        _simpleKeyAllowed = false;
        _prevJson = true;
        Enqueue(kind, _pos, _pos + 1);
        _pos++;
    }

    private void FetchBlockEntry(int column)
    {
        if (_flowLevel != 0)
        {
            throw Error("A block sequence entry is not allowed inside a flow collection.", _pos);
        }

        if (!_simpleKeyAllowed)
        {
            throw Error("A block sequence entry is not allowed here.", _pos + 1);
        }

        if (RollIndent(column))
        {
            if (_tabBefore)
            {
                throw Error("A tab cannot be used to indent a block collection.", _pos);
            }

            Enqueue(TokenKind.BlockSequenceStart, _pos, _pos);
        }

        RemoveSimpleKey();
        _simpleKeyAllowed = true;
        Enqueue(TokenKind.BlockEntry, _pos, _pos + 1);
        _pos++;
    }

    private void FetchKey(int column)
    {
        if (_flowLevel == 0)
        {
            if (!_simpleKeyAllowed)
            {
                throw Error("A mapping key is not allowed here.", _pos + 1);
            }

            if (RollIndent(column))
            {
                if (_tabBefore)
                {
                    throw Error("A tab cannot be used to indent a block collection.", _pos);
                }

                Enqueue(TokenKind.BlockMappingStart, _pos, _pos);
            }
        }

        RemoveSimpleKey();
        _simpleKeyAllowed = _flowLevel == 0;
        Enqueue(TokenKind.Key, _pos, _pos + 1);
        _pos++;
    }

    private void FetchValue(int column)
    {
        if (_keyHead != _keyTail && _keys[_keyTail - 1].Level == _flowLevel && !_keys[_keyTail - 1].Dead)
        {
            SimpleKey key = _keys[_keyTail - 1];
            PopKey();

            // The key's tokens are already queued: mark the first one so the parser sees the
            // mapping start (when the key opens a new block mapping) and the key before it.
            int slot = _qHead + (key.TokenNumber - _tokensParsed);
            _queue[slot].Pre |= 2;
            if (RollIndent(key.Column))
            {
                if (key.TabBefore)
                {
                    throw Error("A tab cannot be used to indent a block collection.", key.Offset);
                }

                _queue[slot].Pre |= 1;
            }

            _simpleKeyAllowed = false;
        }
        else
        {
            if (_flowLevel == 0)
            {
                if (!_simpleKeyAllowed)
                {
                    throw Error("A mapping value is not allowed here.", _pos + 1);
                }

                if (RollIndent(column))
                {
                    if (_tabBefore)
                    {
                        throw Error("A tab cannot be used to indent a block collection.", _pos);
                    }

                    Enqueue(TokenKind.BlockMappingStart, _pos, _pos);
                }
            }

            _simpleKeyAllowed = _flowLevel == 0;
        }

        Enqueue(TokenKind.Value, _pos, _pos + 1);
        _pos++;
    }

    private void FetchAnchor(TokenKind kind)
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;
        int start = _pos;
        int p = start + 1;
        ReadOnlySpan<byte> s = _src;
        while (p < s.Length && !IsBlankZ(s[p]) && !IsFlowIndicator(s[p]))
        {
            p++;
        }

        if (p == start + 1)
        {
            throw Error(kind == TokenKind.Alias ? "An alias needs a name." : "An anchor needs a name.", start + 1);
        }

        Token t = default;
        t.Kind = kind;
        t.Start = start;
        t.End = p;
        t.ContentStart = start + 1;
        t.ContentEnd = p;
        Enqueue(in t);
        _pos = p;
    }

    private void FetchTag()
    {
        SaveSimpleKey();
        _simpleKeyAllowed = false;
        ReadOnlySpan<byte> s = _src;
        int start = _pos;
        Token t = default;
        t.Kind = TokenKind.Tag;
        t.Start = start;
        int p;
        if (At(start + 1) == (byte)'<')
        {
            // Verbatim tag: !<uri>
            p = start + 2;
            while (p < s.Length && s[p] != (byte)'>' && !IsBlankZ(s[p]))
            {
                p++;
            }

            if (At(p) != (byte)'>')
            {
                throw Error("A verbatim tag must end with '>'.", p);
            }

            if (p == start + 2)
            {
                throw Error("A verbatim tag cannot be empty.", p);
            }

            CheckTagText(start + 2, p);
            t.Flags = 1;
            t.ContentStart = start + 2;
            t.ContentEnd = p;
            p++;
        }
        else
        {
            int q = start + 1;
            while (IsTagWordChar(At(q)))
            {
                q++;
            }

            int suffixStart;
            if (At(q) == (byte)'!')
            {
                t.X = q + 1 - start;
                suffixStart = q + 1;
            }
            else
            {
                t.X = 1;
                suffixStart = start + 1;
            }

            p = suffixStart;
            while (IsTagChar(At(p)))
            {
                if (At(p) == (byte)'%')
                {
                    if (!IsHex(At(p + 1)) || !IsHex(At(p + 2)))
                    {
                        throw Error("A '%' in a tag must be followed by two hexadecimal digits.", p);
                    }

                    p += 3;
                }
                else
                {
                    p++;
                }
            }

            if (t.X > 1 && p == suffixStart)
            {
                throw Error("A tag with a handle needs a name after the handle.", p);
            }

            t.ContentStart = suffixStart;
            t.ContentEnd = p;
        }

        byte after = At(p);
        if (!IsBlankZ(after) && !IsFlowIndicator(after))
        {
            throw Error("A tag must be followed by whitespace.", p);
        }

        t.End = p;
        Enqueue(in t);
        _pos = p;
    }

    private void CheckTagText(int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (_src[i] == (byte)'%' && (!IsHex(At(i + 1)) || !IsHex(At(i + 2))))
            {
                throw Error("A '%' in a tag must be followed by two hexadecimal digits.", i);
            }
        }
    }

    private static bool IsHex(byte b) => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static bool IsTagWordChar(byte b)
        => (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == (byte)'-';

    // URI characters except '!' and the flow indicators (YAML 1.2.2 ns-tag-char). '#' is a
    // tag character; it only starts a comment after whitespace.
    private static bool IsTagChar(byte b)
    {
        if (IsTagWordChar(b))
        {
            return true;
        }

        return b switch
        {
            (byte)'#' or (byte)';' or (byte)'/' or (byte)'?' or (byte)':' or (byte)'@' or (byte)'&' or (byte)'=' or (byte)'+' or
            (byte)'$' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'*' or (byte)'\'' or (byte)'(' or (byte)')' or (byte)'%' => true,
            _ => false,
        };
    }

    // ---- Directives ----

    private void FetchDirective()
    {
        UnrollIndent(-1);
        RemoveSimpleKey();
        _simpleKeyAllowed = false;
        ReadOnlySpan<byte> s = _src;
        int start = _pos;
        int p = start + 1;
        int nameStart = p;
        while (p < s.Length && !IsBlankZ(s[p]))
        {
            p++;
        }

        ReadOnlySpan<byte> name = s[nameStart..p];
        Token t = default;
        t.Start = start;
        if (name.SequenceEqual("YAML"u8))
        {
            t.Kind = TokenKind.VersionDirective;
            p = SkipBlanks(p);
            int major = ReadNumber(ref p);
            if (At(p) != (byte)'.')
            {
                throw Error("The %YAML directive needs a version like 1.2.", p);
            }

            p++;
            int minor = ReadNumber(ref p);
            t.X = major;
            t.Y = minor;
        }
        else if (name.SequenceEqual("TAG"u8))
        {
            t.Kind = TokenKind.TagDirective;
            p = SkipBlanks(p);
            int handleStart = p;
            if (At(p) != (byte)'!')
            {
                throw Error("A tag handle must start with '!'.", p);
            }

            p++;
            while (IsTagWordChar(At(p)))
            {
                p++;
            }

            if (At(p) == (byte)'!')
            {
                p++;
            }
            else if (p - handleStart > 1)
            {
                throw Error("A named tag handle must end with '!'.", p);
            }

            t.ContentStart = handleStart;
            t.ContentEnd = p;
            if (!IsBlank(At(p)))
            {
                throw Error("A tag handle must be followed by whitespace and a prefix.", p);
            }

            p = SkipBlanks(p);
            int prefixStart = p;
            while (p < s.Length && !IsBlankZ(s[p]))
            {
                p++;
            }

            if (p == prefixStart)
            {
                throw Error("A %TAG directive needs a prefix.", p);
            }

            CheckTagText(prefixStart, p);
            t.X = prefixStart;
            t.Y = p;
        }
        else
        {
            t.Kind = TokenKind.ReservedDirective;
            if (name.IsEmpty)
            {
                throw Error("A directive needs a name.", p);
            }

            // Unknown directives are skipped to the end of their line.
            int rel = s[p..].IndexOfAny(s_lineBreaks);
            p = rel < 0 ? s.Length : p + rel;
        }

        // Only whitespace and a comment may follow on the line.
        int q = p;
        while (IsBlank(At(q)))
        {
            q++;
        }

        byte c = At(q);
        if (c == (byte)'#' && (q > p || t.Kind == TokenKind.ReservedDirective))
        {
            int rel = s[q..].IndexOfAny(s_lineBreaks);
            int end = rel < 0 ? s.Length : q + rel;
            RecordComment(q, end);
            q = end;
        }
        else if (c != 0 && c != (byte)'\n' && c != (byte)'\r')
        {
            throw Error("Unexpected text after the directive.", q);
        }

        t.End = p;
        Enqueue(in t);
        _pos = q;
    }

    private int SkipBlanks(int p)
    {
        while (IsBlank(At(p)))
        {
            p++;
        }

        return p;
    }

    private int ReadNumber(ref int p)
    {
        int start = p;
        int value = 0;
        while (At(p) is >= (byte)'0' and <= (byte)'9')
        {
            if (value > 100_000)
            {
                throw Error("The version number is too large.", p);
            }

            value = (value * 10) + (At(p) - '0');
            p++;
        }

        if (p == start)
        {
            throw Error("The %YAML directive needs a version like 1.2.", p);
        }

        return value;
    }
}
