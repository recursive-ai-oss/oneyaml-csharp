using System.Diagnostics;
using System.Reflection;
using System.Text;
using Xunit;

namespace OneYaml.Tests;

// The default depth limit has to be reachable on a 1 MB stack, the default of a Windows thread: every path that
// recurses once per level on CLR frames is run one level below the limit on such a thread. A margin that shrinks
// shows up here as a Limit error on the platform that has the least stack.

internal sealed class ChainNode
{
    public ChainNode? C { get; set; }

    public string? S { get; set; }
}

// Bound through its constructor: the reader holds every level's arguments until the innermost value is read.
internal sealed record RecordNode(RecordNode? C, string? S);

internal sealed class ObjectBox
{
    public object? Child { get; set; }
}

internal sealed class TreeNode
{
    public List<TreeNode> Children { get; set; } = [];
}

// Polymorphic chains: a base whose derived type holds the next level, told apart by a key or by a tag.
[YamlPolymorphic]
[YamlDerivedType(typeof(KeyNode), "n")]
internal abstract class KeyBase;

internal sealed class KeyNode : KeyBase
{
    public KeyBase? C { get; set; }

    public string? S { get; set; }
}

[YamlPolymorphic]
[YamlDerivedType(typeof(KeyRecNode), "n")]
internal abstract record KeyRecordBase;

internal sealed record KeyRecNode(KeyRecordBase? C, string? S) : KeyRecordBase;

[YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Tag)]
[YamlDerivedType(typeof(TagNode), "n")]
internal abstract class TagBase;

internal sealed class TagNode : TagBase
{
    public TagBase? C { get; set; }

    public string? S { get; set; }
}

[YamlPolymorphic(DiscriminatorKind = YamlDiscriminatorKind.Tag)]
[YamlDerivedType(typeof(TagRecNode), "n")]
internal abstract record TagRecordBase;

internal sealed record TagRecNode(TagRecordBase? C, string? S) : TagRecordBase;

// A typed graph nested through extension data: the write of each level sits on the stack under the one before.
internal sealed class ExtensionNode
{
    public int V { get; set; }

    [YamlExtensionData]
    public Dictionary<string, object?> Extra { get; set; } = [];
}

[YamlSerializable(typeof(KeyBase))]
[YamlSerializable(typeof(KeyRecordBase))]
[YamlSerializable(typeof(TagBase))]
[YamlSerializable(typeof(TagRecordBase))]
[YamlSerializable(typeof(ChainNode))]
[YamlSerializable(typeof(RecordNode))]
[YamlSerializable(typeof(ObjectBox))]
[YamlSerializable(typeof(TreeNode))]
[YamlSerializable(typeof(object))]
internal sealed partial class HeadroomContext : YamlSerializerContext
{
}

public class StackHeadroomTests
{
    private static int Levels => YamlLimits.Default.MaxDepth - 1;

    private static YamlTypeInfo<T> Generated<T>() => (YamlTypeInfo<T>)new HeadroomContext().GetTypeInfo(typeof(T))!;

    // The depth limit is sized for optimised code. Unoptimised frames are several times larger, so a build
    // without optimisation cannot reach it on a 1 MB stack, and these tests say so instead of failing.
    private static void SkipWhenUnoptimised() =>
        Assert.SkipWhen(
            typeof(YamlSerializer).Assembly.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true },
            "The library is built without optimisation, whose frames are too large to reach the depth limit on a 1 MB stack.");

    private static void OnMegabyteStack(Action run)
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    run();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    // A chain of mappings under "c:", ending in a mapping with an escaped string.
    private static string BlockChain(int levels)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < levels; i++)
        {
            sb.Append(' ', i).Append("c:\n");
        }

        sb.Append(' ', levels).Append("s: \"a\\tb \\u00e9\"\n");
        return sb.ToString();
    }

    private static ChainNode Chain(int levels)
    {
        var node = new ChainNode { S = "x" };
        for (int i = 1; i < levels; i++)
        {
            node = new ChainNode { C = node };
        }

        return node;
    }

    private static int Length(RecordNode? node)
    {
        int n = 0;
        for (; node is not null; node = node.C)
        {
            n++;
        }

        return n;
    }

    private static int Length(ChainNode? node)
    {
        int n = 0;
        for (; node is not null; node = node.C)
        {
            n++;
        }

        return n;
    }

    private static object? ObjectChain(int levels, bool alternate)
    {
        object? value = null;
        for (int i = 0; i < levels; i++)
        {
            value = alternate && i % 2 == 1
                ? new Dictionary<string, object?> { ["c"] = value }
                : new ObjectBox { Child = value };
        }

        return value;
    }

    [Fact]
    public void Typed_read_by_reflection_reaches_the_depth_limit()
    {
        string yaml = BlockChain(Levels);
        OnMegabyteStack(() => Assert.Equal(Levels + 1, Length(YamlSerializer.Deserialize<ChainNode>(yaml))));
    }

    [Fact]
    public void Constructor_bound_records_reach_the_depth_limit_by_both_paths()
    {
        SkipWhenUnoptimised();
        string yaml = BlockChain(Levels);
        YamlTypeInfo<RecordNode> info = Generated<RecordNode>();
        OnMegabyteStack(() =>
        {
            RecordNode? reflected = YamlSerializer.Deserialize<RecordNode>(yaml);
            Assert.Equal(Levels + 1, Length(reflected));
            Assert.Equal(Levels + 1, Length(YamlSerializer.Deserialize(yaml, info)));
        });
    }

    [Fact]
    public void Typed_read_by_the_generator_reaches_the_depth_limit()
    {
        string yaml = BlockChain(Levels);
        YamlTypeInfo<ChainNode> info = Generated<ChainNode>();
        OnMegabyteStack(() => Assert.Equal(Levels + 1, Length(YamlSerializer.Deserialize(yaml, info))));
    }

    [Fact]
    public void Typed_read_through_lists_reaches_the_depth_limit()
    {
        // Each tree node is a mapping and a sequence, two levels.
        int nodes = Levels / 2;
        string yaml = string.Concat(Enumerable.Repeat("{Children: [", nodes)) + string.Concat(Enumerable.Repeat("]}", nodes));
        YamlTypeInfo<TreeNode> info = Generated<TreeNode>();
        OnMegabyteStack(() =>
        {
            Assert.NotNull(YamlSerializer.Deserialize<TreeNode>(yaml));
            Assert.NotNull(YamlSerializer.Deserialize(yaml, info));
        });
    }

    [Fact]
    public void Typed_write_reaches_the_depth_limit_by_both_paths()
    {
        ChainNode chain = Chain(Levels);
        YamlTypeInfo<ChainNode> info = Generated<ChainNode>();
        OnMegabyteStack(() =>
        {
            string reflected = YamlSerializer.Serialize(chain);
            Assert.Equal(reflected, YamlSerializer.Serialize(chain, info));
            Assert.Equal(Levels, Length(YamlSerializer.Deserialize<ChainNode>(reflected)));
        });
    }

    [Fact]
    public void Generic_read_and_write_reach_the_depth_limit()
    {
        string yaml = BlockChain(Levels);
        OnMegabyteStack(() =>
        {
            object? value = YamlSerializer.Deserialize<object>(yaml);
            string written = YamlSerializer.Serialize(value);
            Assert.Equal(written, YamlSerializer.Serialize(YamlSerializer.Deserialize<object>(written)));
        });
    }

    [Fact]
    public void Plain_objects_under_object_members_reach_the_depth_limit()
    {
        object? value = ObjectChain(Levels, alternate: false);
        YamlTypeInfo<object> info = Generated<object>();
        OnMegabyteStack(() =>
        {
            string reflected = YamlSerializer.Serialize<object>(value!);
            Assert.Equal(reflected, YamlSerializer.Serialize(value!, info));
        });
    }

    [Fact]
    public void Objects_and_dictionaries_alternating_under_object_members_reach_the_depth_limit()
    {
        SkipWhenUnoptimised();
        object? value = ObjectChain(Levels, alternate: true);
        YamlTypeInfo<object> info = Generated<object>();
        OnMegabyteStack(() =>
        {
            string reflected = YamlSerializer.Serialize<object>(value!);
            Assert.Equal(reflected, YamlSerializer.Serialize(value!, info));
        });
    }

    [Fact]
    public void A_document_reaches_the_depth_limit_in_parse_bind_edit_and_print()
    {
        string yaml = BlockChain(Levels);
        string path = string.Concat(Enumerable.Repeat("/c", Levels)) + "/s";
        OnMegabyteStack(() =>
        {
            using var doc = YamlDocument.Parse(yaml);
            Assert.Equal(Levels + 1, Length(doc.Root.Deserialize<ChainNode>()));
            Assert.NotNull(doc.Root.Deserialize<object>());
            doc.Set(path, 2);
            Assert.Equal(2, doc.Get<int>(path));
            Assert.Contains("s: 2", doc.ToString(), StringComparison.Ordinal);
        });
    }

    // Mappings nested to the limit, each naming its derived type by a key or by a tag.
    private static string PolymorphicChain(int levels, string open, string close, string leaf) =>
        string.Concat(Enumerable.Repeat(open, levels - 1)) + leaf + string.Concat(Enumerable.Repeat(close, levels - 1));

    private static string KeyChain(int levels) => PolymorphicChain(levels, "{$type: n, c: ", "}", "{$type: n, s: \"a\\tb\"}");

    // The same chain with the key naming the derived type after another key, so every level looks ahead for it.
    private static string KeyChainAfterAnotherKey(int levels) => PolymorphicChain(levels, "{s: x, $type: n, c: ", "}", "{s: \"a\\tb\", $type: n}");

    private static string TagChain(int levels) => PolymorphicChain(levels, "!n {c: ", "}", "!n {s: \"a\\tb\"}");

    private static int Depth(object? node)
    {
        int n = 0;
        for (; node is not null; n++)
        {
            node = node switch
            {
                KeyNode k => k.C,
                KeyRecNode r => r.C,
                TagNode t => t.C,
                TagRecNode tr => tr.C,
                _ => null,
            };
        }

        return n;
    }

    [Fact]
    public void Key_polymorphic_classes_reach_the_depth_limit_by_every_path()
    {
        SkipWhenUnoptimised();
        string yaml = KeyChain(Levels);
        YamlTypeInfo<KeyBase> info = Generated<KeyBase>();
        OnMegabyteStack(() =>
        {
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize<KeyBase>(yaml)));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize(yaml, info)));
            Assert.Equal(Levels, Depth(YamlDocument.Parse(yaml).Root.Deserialize<KeyBase>()));
        });
    }

    [Fact]
    public void A_key_that_names_the_derived_type_after_another_key_reaches_the_depth_limit_by_every_path()
    {
        SkipWhenUnoptimised();
        string yaml = KeyChainAfterAnotherKey(Levels);
        YamlTypeInfo<KeyBase> info = Generated<KeyBase>();
        YamlTypeInfo<KeyRecordBase> recordInfo = Generated<KeyRecordBase>();
        OnMegabyteStack(() =>
        {
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize<KeyBase>(yaml)));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize(yaml, info)));
            Assert.Equal(Levels, Depth(YamlDocument.Parse(yaml).Root.Deserialize<KeyBase>()));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize<KeyRecordBase>(yaml)));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize(yaml, recordInfo)));
        });
    }

    [Fact]
    public void Key_polymorphic_records_reach_the_depth_limit_by_every_path()
    {
        SkipWhenUnoptimised();
        string yaml = KeyChain(Levels);
        YamlTypeInfo<KeyRecordBase> info = Generated<KeyRecordBase>();
        OnMegabyteStack(() =>
        {
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize<KeyRecordBase>(yaml)));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize(yaml, info)));
            Assert.Equal(Levels, Depth(YamlDocument.Parse(yaml).Root.Deserialize<KeyRecordBase>()));
        });
    }

    [Fact]
    public void Tag_polymorphic_classes_reach_the_depth_limit_by_every_path()
    {
        SkipWhenUnoptimised();
        string yaml = TagChain(Levels);
        YamlTypeInfo<TagBase> info = Generated<TagBase>();
        OnMegabyteStack(() =>
        {
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize<TagBase>(yaml)));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize(yaml, info)));
            Assert.Equal(Levels, Depth(YamlDocument.Parse(yaml).Root.Deserialize<TagBase>()));
        });
    }

    [Fact]
    public void Tag_polymorphic_records_reach_the_depth_limit_by_every_path()
    {
        SkipWhenUnoptimised();
        string yaml = TagChain(Levels);
        YamlTypeInfo<TagRecordBase> info = Generated<TagRecordBase>();
        OnMegabyteStack(() =>
        {
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize<TagRecordBase>(yaml)));
            Assert.Equal(Levels, Depth(YamlSerializer.Deserialize(yaml, info)));
            Assert.Equal(Levels, Depth(YamlDocument.Parse(yaml).Root.Deserialize<TagRecordBase>()));
        });
    }

    [Fact]
    public void Writing_through_extension_data_reaches_the_depth_limit()
    {
        SkipWhenUnoptimised();
        var root = new ExtensionNode();
        ExtensionNode current = root;
        for (int i = 1; i < Levels; i++)
        {
            var next = new ExtensionNode { V = i };
            current.Extra["c"] = next;
            current = next;
        }

        OnMegabyteStack(() => Assert.Contains("c:", YamlSerializer.Serialize(root)));
    }
}
