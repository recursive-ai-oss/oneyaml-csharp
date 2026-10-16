using System;
using System.Collections.Generic;
using System.Text;
using OneYaml.Parsing;

namespace OneYaml;

// Finds the JSON Pointer of the node at a byte offset by reading the text again. It runs only when an
// error is being built, so reading itself keeps no path and pays nothing for it.
internal static class ErrorPath
{
    private struct Frame
    {
        public bool IsMapping;
        public bool ExpectKey;
        public bool HasChild;
        public int Index;
        public string? Key;
        public bool KeyUnnamed;
    }

    // atCollection: the offset is the start of a collection the caller is complaining about, so its first
    // entry, which starts at the same offset, is not part of the path.
    public static string? Find(ReadOnlySpan<byte> utf8, int offset, bool atCollection)
    {
        if (offset < 0 || offset > utf8.Length)
        {
            return null;
        }

        // Limits are off: the text was read under the caller's limits already, and this only follows events.
        YamlReader reader;
        try
        {
            reader = new YamlReader(utf8, new YamlReaderOptions { Limits = YamlLimits.None });
        }
        catch (YamlException)
        {
            return null;
        }

        try
        {
            return Walk(ref reader, offset, atCollection);
        }
        catch (YamlException)
        {
            // The text does not parse this far (a syntax error); there is no node to name.
            return null;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static string? Walk(ref YamlReader reader, int offset, bool atCollection)
    {
        var stack = new List<Frame>();
        int pendingEnds = 0;
        bool seen = false;
        bool stopAfterNext = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case YamlTokenType.DocumentStart:
                    if (reader.Start > offset)
                    {
                        return Build(stack);
                    }

                    stack.Clear();
                    pendingEnds = 0;
                    seen = false;
                    break;
                case YamlTokenType.MappingEnd:
                case YamlTokenType.SequenceEnd:
                    pendingEnds++;
                    break;
                case YamlTokenType.Scalar:
                case YamlTokenType.Alias:
                case YamlTokenType.MappingStart:
                case YamlTokenType.SequenceStart:
                    {
                        if (stopAfterNext || (reader.Start > offset && seen))
                        {
                            return Build(stack);
                        }

                        if (reader.Start > offset)
                        {
                            return string.Empty;
                        }

                        for (; pendingEnds > 0; pendingEnds--)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        seen = true;
                        if (stack.Count > 0)
                        {
                            Frame top = stack[^1];
                            top.HasChild = true;
                            if (top.IsMapping)
                            {
                                if (top.ExpectKey)
                                {
                                    // A key that is a collection or an alias has no name to write; a segment
                                    // made of nothing would read as an empty-string key.
                                    top.KeyUnnamed = reader.TokenType != YamlTokenType.Scalar;
                                    top.Key = top.KeyUnnamed ? null : reader.GetString();
                                    top.ExpectKey = false;
                                }
                                else
                                {
                                    top.ExpectKey = true;
                                }
                            }
                            else
                            {
                                top.Index++;
                            }

                            stack[^1] = top;
                        }

                        bool collection = reader.TokenType is YamlTokenType.MappingStart or YamlTokenType.SequenceStart;
                        if (collection)
                        {
                            stack.Add(new Frame { IsMapping = reader.TokenType == YamlTokenType.MappingStart, ExpectKey = true, Index = -1 });
                            if (atCollection && reader.Start == offset)
                            {
                                stopAfterNext = true;
                            }
                        }

                        break;
                    }

                default:
                    break;
            }
        }

        return Build(stack);
    }

    private static string? Build(List<Frame> stack)
    {
        var sb = new StringBuilder();
        foreach (Frame f in stack)
        {
            if (f.IsMapping)
            {
                if (f.HasChild && f.KeyUnnamed)
                {
                    return null;
                }

                if (!f.HasChild || f.Key is null)
                {
                    continue;
                }

                sb.Append('/');
                foreach (char c in f.Key)
                {
                    _ = c switch
                    {
                        '~' => sb.Append("~0"),
                        '/' => sb.Append("~1"),
                        _ => sb.Append(c),
                    };
                }
            }
            else if (f.Index >= 0)
            {
                sb.Append('/').Append(f.Index);
            }
        }

        return sb.ToString();
    }
}
