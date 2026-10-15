using System;
using System.Collections.Generic;
using OneYaml.Emitting;
using OneYaml.Schema;

namespace OneYaml.Documents;

/// <summary>The layout of the document around an edit, which new text copies.</summary>
/// <param name="Nl">The line break the document uses nearby.</param>
/// <param name="Step">Columns a nested mapping is indented by.</param>
/// <param name="SeqOffset">Columns a block sequence under a key is indented by (0 when its dashes are flush with the key).</param>
internal readonly record struct Layout(string Nl, int Step, int SeqOffset);

/// <summary>Writes values as YAML text with explicit stacks, so nesting depth costs no CLR stack.</summary>
internal static class ValueWriter
{
    private struct Frame
    {
        public int Remaining;
        public int Col;
        public bool IsMap;
        public bool First;
        public int Written;
    }

    /// <summary>Writes one scalar value in the given context.</summary>
    /// <param name="o">The destination (LF line breaks; the caller converts them).</param>
    /// <param name="n">A scalar node of a value tree.</param>
    /// <param name="preferred">The style to keep if it can hold the value.</param>
    /// <param name="ctx">Where the scalar sits.</param>
    /// <param name="parentIndent">Column of the owning key or dash, for block scalars.</param>
    /// <param name="contentIndent">Column of a block scalar's lines.</param>
    /// <returns>The style used.</returns>
    public static ScalarWriteStyle WriteScalar(ByteBuffer o, in VNode n, ScalarWriteStyle preferred, EmitContext ctx, int parentIndent, int contentIndent)
    {
        if (n.Kind != VKind.Scalar)
        {
            throw new YamlException("A mapping or sequence cannot be used as a key here.", YamlErrorCategory.PatchInvalid, 0, 0, -1);
        }

        if (n.Scalar != ScalarKind.String)
        {
            o.Write(n.Text);
            return ScalarWriteStyle.Plain;
        }

        var w = new Utf8YamlWriter(o);
        return w.WriteString(n.Text, preferred, ctx, StyleMode.Edit, parentIndent, contentIndent);
    }

    /// <summary>
    /// Writes a mapping key. A key too long to be implicit is written as <c>? key</c>: in block
    /// context the colon follows on a line of its own (this method writes it), in flow context the
    /// caller writes the colon as it does for any key.
    /// </summary>
    /// <param name="o">The destination.</param>
    /// <param name="key">A scalar node of a value tree.</param>
    /// <param name="preferred">The style to keep if it can hold the key.</param>
    /// <param name="ctx">A key context.</param>
    /// <param name="col">Column of the entry.</param>
    /// <param name="step">Columns a nested mapping is indented by.</param>
    public static void WriteKey(ByteBuffer o, in VNode key, ScalarWriteStyle preferred, EmitContext ctx, int col, int step)
    {
        ScalarWriteStyle used = WriteScalar(o, key, preferred, ctx, col, col + step);
        bool flow = ctx == EmitContext.FlowKey;
        if (used != ScalarWriteStyle.ExplicitKeyRequired)
        {
            if (!flow)
            {
                o.Write((byte)':');
            }

            return;
        }

        // The explicit form has no length limit, so the key is written as a value would be.
        o.Write("? "u8);
        ScalarWriteStyle k = WriteScalar(o, key, ScalarWriteStyle.Plain, flow ? EmitContext.FlowValue : EmitContext.BlockValue, col, col + step);
        if (flow)
        {
            o.Write((byte)' ');
            return;
        }

        if (!EndsWithBreak(k))
        {
            o.Write((byte)'\n');
        }

        o.WriteSpaces(col);
        o.Write((byte)':');
    }

    /// <summary>Whether a scalar written in this style ends with a line break of its own.</summary>
    /// <param name="style">The style.</param>
    /// <returns><see langword="true"/> for the block styles.</returns>
    public static bool EndsWithBreak(ScalarWriteStyle style) => style is ScalarWriteStyle.Literal or ScalarWriteStyle.Folded;

    /// <summary>
    /// Writes a non-empty collection as block lines. Each line ends with a line feed. The first
    /// line is written without indentation when <paramref name="inlineFirst"/> is set, because
    /// the caller has already written a dash and a space.
    /// </summary>
    /// <param name="o">The destination.</param>
    /// <param name="v">The value.</param>
    /// <param name="root">The collection's index in the tree.</param>
    /// <param name="col">The column of the collection's entries.</param>
    /// <param name="inlineFirst">Whether the first line continues the caller's line.</param>
    /// <param name="layout">Indentation to copy.</param>
    /// <param name="keyStyle">The style to prefer for the keys of the first-level entries.</param>
    /// <param name="valueStyle">The style to prefer for the scalar values of the first-level entries.</param>
    public static void WriteBlock(ByteBuffer o, ValueTree v, int root, int col, bool inlineFirst, in Layout layout, ScalarWriteStyle keyStyle = ScalarWriteStyle.Plain, ScalarWriteStyle valueStyle = ScalarWriteStyle.Plain)
    {
        var stack = new List<Frame>();
        stack.Add(new Frame { Remaining = v[root].Count, Col = col, IsMap = v[root].Kind == VKind.Mapping, First = inlineFirst });
        int next = root + 1;
        while (stack.Count > 0)
        {
            int top = stack.Count - 1;
            Frame f = stack[top];
            if (f.Remaining == 0)
            {
                stack.RemoveAt(top);
                continue;
            }

            f.Remaining -= f.IsMap ? 2 : 1;
            if (f.First)
            {
                f.First = false;
            }
            else
            {
                o.WriteSpaces(f.Col);
            }

            stack[top] = f;
            if (f.IsMap)
            {
                WriteKey(o, v[next], stack.Count == 1 ? keyStyle : ScalarWriteStyle.Plain, EmitContext.BlockKey, f.Col, layout.Step);
                next += v[next].Size;
            }
            else
            {
                o.Write((byte)'-');
            }

            ref readonly VNode val = ref v[next];
            if (val.Kind == VKind.Alias)
            {
                o.Write(" *"u8);
                o.Write(val.Text);
                o.Write((byte)'\n');
                next++;
            }
            else if (val.Kind == VKind.Scalar)
            {
                o.Write((byte)' ');
                ScalarWriteStyle used = WriteScalar(o, val, stack.Count == 1 ? valueStyle : ScalarWriteStyle.Plain, EmitContext.BlockValue, f.Col, f.Col + layout.Step);
                if (!EndsWithBreak(used))
                {
                    o.Write((byte)'\n');
                }

                next++;
            }
            else if (val.Count == 0)
            {
                o.Write(val.Kind == VKind.Mapping ? " {}\n"u8 : " []\n"u8);
                next++;
            }
            else if (f.IsMap)
            {
                o.Write((byte)'\n');
                int childCol = f.Col + (val.Kind == VKind.Mapping ? layout.Step : layout.SeqOffset);
                stack.Add(new Frame { Remaining = val.Count, Col = childCol, IsMap = val.Kind == VKind.Mapping });
                next++;
            }
            else
            {
                o.Write((byte)' ');
                stack.Add(new Frame { Remaining = val.Count, Col = f.Col + 2, IsMap = val.Kind == VKind.Mapping, First = true });
                next++;
            }
        }
    }

    /// <summary>Writes a value in flow style: <c>[a, b]</c> and <c>{k: v}</c>.</summary>
    /// <param name="o">The destination.</param>
    /// <param name="v">The value.</param>
    /// <param name="root">The node's index in the tree.</param>
    public static void WriteFlow(ByteBuffer o, ValueTree v, int root)
    {
        var stack = new List<Frame>();
        int next = root;
        bool pending = true;
        while (pending || stack.Count > 0)
        {
            if (!pending)
            {
                int top = stack.Count - 1;
                Frame f = stack[top];
                if (f.Remaining == 0)
                {
                    o.Write(f.IsMap ? (byte)'}' : (byte)']');
                    stack.RemoveAt(top);
                    continue;
                }

                if (f.Written > 0)
                {
                    o.Write(", "u8);
                }

                if (f.IsMap)
                {
                    WriteKey(o, v[next], ScalarWriteStyle.Plain, EmitContext.FlowKey, 0, 0);
                    o.Write(": "u8);
                    next += v[next].Size;
                    f.Remaining -= 2;
                    f.Written += 2;
                }
                else
                {
                    f.Remaining--;
                    f.Written++;
                }

                stack[top] = f;
            }

            pending = false;
            ref readonly VNode n = ref v[next];
            if (n.Kind == VKind.Alias)
            {
                o.Write((byte)'*');
                o.Write(n.Text);
                next++;
            }
            else if (n.Kind == VKind.Scalar)
            {
                WriteScalar(o, n, ScalarWriteStyle.Plain, EmitContext.FlowValue, 0, 0);
                next++;
            }
            else
            {
                bool map = n.Kind == VKind.Mapping;
                o.Write(map ? (byte)'{' : (byte)'[');
                next++;
                if (n.Count == 0)
                {
                    o.Write(map ? (byte)'}' : (byte)']');
                }
                else
                {
                    stack.Add(new Frame { Remaining = n.Count, IsMap = map });
                }
            }
        }
    }

    /// <summary>Copies text, replacing each line feed with the document's line break.</summary>
    /// <param name="text">Text with LF line breaks.</param>
    /// <param name="nl">The break to use.</param>
    /// <param name="dest">The destination.</param>
    public static void WriteWithBreaks(ReadOnlySpan<byte> text, string nl, ByteBuffer dest)
    {
        if (nl == "\n")
        {
            dest.Write(text);
            return;
        }

        while (true)
        {
            int i = text.IndexOf((byte)'\n');
            if (i < 0)
            {
                dest.Write(text);
                return;
            }

            dest.Write(text.Slice(0, i));
            dest.WriteAscii(nl);
            text = text.Slice(i + 1);
        }
    }
}
