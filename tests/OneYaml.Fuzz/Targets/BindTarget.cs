using System.Text;
using OneYaml.Tests.Attributes;

namespace OneYaml.Fuzzing;

/// <summary>
/// Typed binding: the same input read through reflection and through the generated context must give the same
/// value (and write the same text) or the same error category. Any other exception is a failure.
/// </summary>
internal sealed class BindTarget : FuzzTarget
{
    private static readonly Func<YamlSerializerOptions, YamlSerializerContext> s_fuzzContext = o => new FuzzContext(o);
    private static readonly Func<YamlSerializerOptions, YamlSerializerContext> s_attributeContext = o => new AttributeContext(o);

    private static readonly BindCase[] s_cases =
    [
        new BindCase<Person>("Person", s_fuzzContext, "name: Ada\nage: 36\nnickname: ~\ntags: [a, b]\nextra: ignored\n", "Name: x\nAge: 1\n"),
        new BindCase<Tree>("Tree", s_fuzzContext, "name: r\nchildren:\n  - name: a\n  - name: b\n    children: [{name: c}]\nbyName: {x: {name: y}, z: ~}\n"),
        new BindCase<Kinds>(
            "Kinds",
            s_fuzzContext,
            "s8: -128\nu8: 255\ns16: -32768\nu16: 65535\ns32: 0x7fffffff\nu32: 0o37777777777\ns64: -9223372036854775808\nu64: 18446744073709551615\nbig: 123456789012345678901234567890\ndec: 12.5\ndbl: .inf\nflt: 1.5e2\nflag: true\nch: x\nid: 4f2a8a1e-6b9d-4a39-9b5c-0d1e2f3a4b5c\nwhen: 2001-12-14T21:59:43Z\nat: 2001-12-14 21:59:43 +05:00\nday: 2001-12-14\nclock: \"21:59:43\"\nspan: 1.02:03:04\nlink: https://example.com/x\nbytes: aGVsbG8=\ncolor: darkGreen\nperm: Read, Write\nmaybe: 5\nanything: {a: [1, 2.5, x]}\n",
            "dbl: .nan\nflt: -.inf\nbig: -0x7fffffffffffffffffff\nperm: 3\ncolor: 1\nmaybe: ~\nanything: &a [*a]\n"),
        new BindCase<Collections>(
            "Collections",
            s_fuzzContext,
            "arr: [1, 2]\nlist: [a, b]\ndict: {a: 1, b: 2}\nset: [x, y, x]\nsorted: {b: 1, a: 2}\nsortedSet: [3, 1, 2, 1]\npeople: [{name: p}]\nbyHue: {red: 1, darkGreen: 2}\nnumbers: [1, 1, 2]\ngrid: [[1], [2, 3]]\nbyNumber: {1: [a], 2: [b, c]}\n",
            "arr: []\nsorted: {a: 1, a: 2}\nbyNumber: {x: [a]}\n"),
        new BindCase<Config>("Config", s_fuzzContext, "replicas: 3\nscript: run\ntone: red\ntags: [a, b]\n", "replicas: x\nscript: run\n", "script: only\n"),
        new BindCase<Wire>("Wire", s_fuzzContext, "host: h\nport: 8\naliases: [a]\nowner: {name: o}\n", "host: [h]\n"),
        new BindCase<Pt>("Pt", s_fuzzContext, "x: 1\ny: 2\n", "x: 1\nx: 2\n"),
        new BindCase<Loose>("Loose", s_fuzzContext, "s: str\nn: 5\n", "n: x\n"),
        new BindCase<Initial>("Initial", s_fuzzContext, "kind: k\ncount: 3\ntags: [a]\nwhere: {x: 1, y: 2}\n", "count: 3\n"),
        new BindCase<object>("object", s_fuzzContext, "a: 1\nb: [x, {y: z}]\nc: &a [1]\nd: *a\n", "- !!str 5\n- 0x1f\n- .inf\n- 2001-12-14\n- {<<: {a: 1}, b: 2}\n"),
        new BindCase<Dictionary<string, object?>>("Dictionary", s_fuzzContext, "a: 1\nb: [x]\nc: {d: null}\n", "a: 1\na: 2\n"),
        new BindCase<List<Person>>("List", s_fuzzContext, "- name: a\n- name: b\n  age: 3\n", "[]", "- 5\n"),
        new BindCase<Job>("Job", s_attributeContext, "level: lv3\nname: abc\nplain: p\n", "level: 3\n"),
        new BindCase<Step>("Step", s_attributeContext, "id: a\ntext: Hello\n", "text: x\n"),
        new BindCase<Crate>("Crate", s_attributeContext, "content: hi\n", "content: [x]\n"),
        new BindCase<Settings>("Settings", s_attributeContext, "name: n\nport: 80\nx1: [1, 2]\nx2: {a: b}\nx3: ~\nname2: &z 5\nx4: *z\n", "port: x\n"),
        new BindCase<Lazy>("Lazy", s_attributeContext, "name: n\nrest1: 1\nrest2: [a]\n"),
        new BindCase<FromJson>("FromJson", s_attributeContext, "name: n\nother: 1\n"),
        new BindCase<Labelled>("Labelled", s_attributeContext, "name: n\nsize: 2\nextra: {a: 1}\n"),
        new BindCase<Home>("Home", s_attributeContext, "pet:\n  $type: cat\n  name: Tom\n  lives: 7\nothers:\n- $type: dog\n  name: Rex\n  good: false\n", "pet:\n  name: Nemo\n", "pet:\n  $type: fish\n"),
        new BindCase<Pet>("Pet", s_attributeContext, "$type: dog\nname: Rex\n", "name: x\n$type: cat\n"),
        new BindCase<Drawing>("Drawing", s_attributeContext, "figures:\n- !circle {radius: 2}\n- !square\n  side: 3\n- !label hello\n", "figures:\n- {radius: 2}\n", "figures:\n- !blob {radius: 2}\n"),
        new BindCase<Vehicle>("Vehicle", s_attributeContext, "kind: car\ncolour: Red\n", "kind: 7\nload: 3\n", "wheels: 6\n"),
        new BindCase<Note>("Note", s_attributeContext, "$type: memo\ntitle: a\npriority: 2\n", "title: b\n"),
    ];

    private static readonly YamlSerializerOptions[][] s_options = [Variants(YamlLimits.Default), Variants(YamlLimits.Untrusted)];
    private static readonly YamlSerializerContext?[][][] s_contexts = BuildContextTable();

    private long _matched;
    private long _bothFailed;

    public override string Name => "bind";

    // Three types are read and written through two paths per input, each up to the untrusted preset's alias budget.
    public override long AllocationFloor => 160L << 20;

    public override IEnumerable<(byte[] Data, int Hint)> ExtraSeeds()
    {
        for (int i = 0; i < s_cases.Length; i++)
        {
            foreach (string seed in s_cases[i].Seeds)
            {
                yield return (Encoding.UTF8.GetBytes(seed), i);
            }
        }
    }

    public override bool Run(byte[] input, int hint, YamlLimits limits)
    {
        Random rng = Seeded(input, hint);
        int limitsIndex = limits == YamlLimits.Untrusted ? 1 : 0;
        int variant = rng.Next(5);
        YamlSerializerOptions options = s_options[limitsIndex][variant];
        bool accepted = false;
        var chosen = new List<int>();
        if (hint >= 0 && hint < s_cases.Length)
        {
            chosen.Add(hint);
        }

        while (chosen.Count < 3)
        {
            int pick = rng.Next(s_cases.Length);
            if (!chosen.Contains(pick))
            {
                chosen.Add(pick);
            }
        }

        foreach (int index in chosen)
        {
            BindCase c = s_cases[index];
            YamlSerializerContext context = ContextFor(limitsIndex, variant, c);
            string reflected = c.Read(this, input, options, context, reflect: true);
            string generated = c.Read(this, input, options, context, reflect: false) + (Plant(input) ? "x" : string.Empty);
            if (reflected != generated)
            {
                string kind = reflected.StartsWith("error", StringComparison.Ordinal)
                    ? (generated.StartsWith("error", StringComparison.Ordinal) ? "error-category" : "error-vs-value")
                    : (generated.StartsWith("error", StringComparison.Ordinal) ? "value-vs-error" : "value");
                throw new FuzzFailure($"bind-{c.Name}-{kind}", $"{c.Name} variant {variant}\nreflection: {Cut(reflected)}\ngenerated:  {Cut(generated)}");
            }

            if (reflected.StartsWith("ok", StringComparison.Ordinal))
            {
                _matched++;
                accepted = true;
            }
            else
            {
                _bothFailed++;
            }
        }

        return accepted;
    }

    public override string Stats() => $"matchedValues={_matched} matchedErrors={_bothFailed}";

    private static string Cut(string s) => s.Length > 3000 ? s[..3000] + "..." : s;

    private static YamlSerializerOptions[] Variants(YamlLimits limits) =>
    [
        new YamlSerializerOptions { Limits = limits },
        new YamlSerializerOptions { Limits = limits, PropertyNamingPolicy = null },
        new YamlSerializerOptions { Limits = limits, PropertyNameCaseInsensitive = true },
        new YamlSerializerOptions { Limits = limits, UnmappedMemberHandling = YamlUnmappedMemberHandling.Disallow },
        new YamlSerializerOptions { Limits = limits, MergeKeys = true, PropertyNameCaseInsensitive = true },
    ];

    // One context per options object and context kind: creating one per input would measure the metadata build.
    private static YamlSerializerContext?[][][] BuildContextTable()
    {
        var table = new YamlSerializerContext?[2][][];
        for (int l = 0; l < 2; l++)
        {
            table[l] = new YamlSerializerContext?[5][];
            for (int v = 0; v < 5; v++)
            {
                table[l][v] = [null, null];
            }
        }

        return table;
    }

    private static YamlSerializerContext ContextFor(int limitsIndex, int variant, BindCase c)
    {
        int kind = ReferenceEquals(c.MakeContext, s_fuzzContext) ? 0 : 1;
        return table(limitsIndex, variant, kind) ??= c.MakeContext(s_options[limitsIndex][variant]);

        static ref YamlSerializerContext? table(int l, int v, int k) => ref s_contexts[l][v][k];
    }

    private abstract class BindCase
    {
        public abstract string Name { get; }

        public abstract string[] Seeds { get; }

        public abstract Func<YamlSerializerOptions, YamlSerializerContext> MakeContext { get; }

        public abstract string Read(BindTarget owner, byte[] input, YamlSerializerOptions options, YamlSerializerContext context, bool reflect);
    }

    private sealed class BindCase<T> : BindCase
    {
        public BindCase(string name, Func<YamlSerializerOptions, YamlSerializerContext> make, params string[] seeds)
        {
            Name = name;
            MakeContext = make;
            Seeds = seeds;
        }

        public override string Name { get; }

        public override string[] Seeds { get; }

        public override Func<YamlSerializerOptions, YamlSerializerContext> MakeContext { get; }

        public override string Read(BindTarget owner, byte[] input, YamlSerializerOptions options, YamlSerializerContext context, bool reflect)
        {
            var info = (YamlTypeInfo<T>)context.GetTypeInfo(typeof(T))!;
            try
            {
                T? value;
                string written;
                using (owner.Library())
                {
                    value = reflect ? YamlSerializer.Deserialize<T>(input, options) : YamlSerializer.Deserialize(input, info);
                    written = reflect ? YamlSerializer.Serialize<T>(value!, options) : YamlSerializer.Serialize<T>(value!, info);
                }

                return "ok\n" + Values.Render(value) + "\n" + written;
            }
            catch (YamlException ex)
            {
                return "error " + ex.Category;
            }
        }
    }
}
