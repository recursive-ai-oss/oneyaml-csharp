using Xunit;

namespace OneYaml.Tests.Document;

public sealed class DeployStrategy
{
    public string Type { get; set; } = string.Empty;

    public int MaxSurge { get; set; }
}

public sealed record Probe(string Path, int Port);

[YamlSerializable(typeof(DeployStrategy))]
[YamlSerializable(typeof(Probe))]
[YamlSerializable(typeof(List<Probe>))]
[YamlSerializable(typeof(TimeSpan))]
internal sealed partial class DocumentValuesContext : YamlSerializerContext
{
}

/// <summary>Typed values into a document, the errors that name what went wrong, the document index and the async members.</summary>
public class DocumentApiTests
{
    // ---- Typed values ----

    [Fact]
    public void Plain_objects_of_any_type_the_serializer_writes_go_into_a_document()
    {
        using YamlDocument doc = YamlDocument.Parse("spec:\n  replicas: 3\n");
        doc.Set("/spec/strategy", new DeployStrategy { Type = "RollingUpdate", MaxSurge = 1 });
        doc.Set("/spec/timeout", TimeSpan.FromSeconds(30));
        doc.Set("/spec/probes", new List<Probe> { new("/ready", 8080), new("/live", 8081) });
        doc.Set("/spec/day", DayOfWeek.Monday);
        Assert.Equal(
            "spec:\n  replicas: 3\n  strategy:\n    type: RollingUpdate\n    maxSurge: 1\n  timeout: 00:00:30\n  probes:\n    - path: /ready\n      port: 8080\n    - path: /live\n      port: 8081\n  day: Monday\n",
            doc.ToString());
        Assert.Equal(TimeSpan.FromSeconds(30), doc.Root["spec"]["timeout"].Deserialize<TimeSpan>());
        Assert.Equal(8081, doc.Root["spec"]["probes"][1].Deserialize<Probe>()!.Port);
    }

    [Fact]
    public void Typed_values_reach_collections_and_nodes_of_every_entry_point()
    {
        using YamlDocument doc = YamlDocument.Parse("list: [1]\nm: {}\n");
        doc.Add("/list/-", new DeployStrategy { Type = "A" });
        doc.Root["m"]["k"] = YamlNode.Create(new Probe("/p", 1));
        doc.Root["m"]["d"] = new Dictionary<string, object> { ["inner"] = new DeployStrategy { Type = "B", MaxSurge = 2 } }.ToNode();
        doc.ApplyPatch(new YamlPatch().Add("/m/patched", new Probe("/q", 2)).Replace("/list/0", TimeSpan.FromMinutes(1)));
        using YamlDocument parsed = YamlDocument.Parse(doc.ToString());
        Assert.Equal("A", parsed.Root["list"][1]["type"].GetValue<string>());
        Assert.Equal("/p", parsed.Root["m"]["k"]["path"].GetValue<string>());
        Assert.Equal(2, parsed.Root["m"]["d"]["inner"]["maxSurge"].GetValue<int>());
        Assert.Equal("/q", parsed.Root["m"]["patched"]["path"].GetValue<string>());
        Assert.Equal("00:01:00", parsed.Root["list"][0].GetValue<string>());
    }

    [Fact]
    public void Generated_metadata_writes_typed_values_without_reflection()
    {
        DocumentValuesContext context = DocumentValuesContext.Default;
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        doc.Set("/s", new DeployStrategy { Type = "Recreate", MaxSurge = 0 }, context.DeployStrategy);
        doc.Add("/probes", new List<Probe> { new("/x", 9) }, context.ListProbe);
        doc.Root["t"] = YamlNode.Create(TimeSpan.FromHours(1), context.TimeSpan);
        doc.Root["a"].Set(new Probe("/y", 3), context.Probe);
        doc.ApplyPatch(new YamlPatch().Add("/more", new Probe("/z", 4), context.Probe).Test("/more/port", 4));
        Assert.Equal("a:\n  path: /y\n  port: 3\ns:\n  type: Recreate\n  maxSurge: 0\nprobes:\n  - path: /x\n    port: 9\nt: 01:00:00\nmore:\n  path: /z\n  port: 4\n", doc.ToString());
    }

    [Fact]
    public void Options_decide_how_a_typed_value_is_named()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        doc.Set("/s", new DeployStrategy { Type = "X" }, new YamlSerializerOptions { PropertyNamingPolicy = null });
        doc.Root["n"] = YamlNode.Create(new DeployStrategy { Type = "Y" }, new YamlSerializerOptions { PropertyNamingPolicy = null });
        Assert.Equal("a: 1\ns:\n  Type: X\n  MaxSurge: 0\nn:\n  Type: Y\n  MaxSurge: 0\n", doc.ToString());
    }

    [Fact]
    public void A_value_the_serializer_cannot_write_is_an_argument_error_and_the_document_is_unchanged()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n");
        Assert.Throws<ArgumentException>(() => doc.Set("/a", new NoYamlForm()));
        Assert.Throws<ArgumentException>(() => YamlNode.Create(new NoYamlForm()));
        Assert.Throws<ArgumentException>(() => new YamlPatch().Test("/a", new NoYamlForm()));
        Assert.Equal("a: 1\n", doc.ToString());
    }

    // ---- Errors that say what is missing ----

    [Fact]
    public void Reading_a_missing_key_names_the_key_and_the_lookup_route_does_not_throw()
    {
        using YamlDocument doc = YamlDocument.Parse("spec:\n  replicas: 3\nlist: [1]\n");
        KeyNotFoundException ex = Assert.Throws<KeyNotFoundException>(() => doc.Root["spec"]["nope"]);
        Assert.Contains("'nope'", ex.Message);
        Assert.False(doc.Root["spec"].TryGetValue("nope", out YamlNode? none));
        Assert.Null(none);
        Assert.True(doc.Root["spec"].TryGetValue("replicas", out YamlNode? replicas));
        Assert.Equal(3, replicas!.GetValue<int>());
        Assert.True(doc.Root["list"].TryGetValue(0, out YamlNode? first));
        Assert.Equal(1, first!.GetValue<int>());
        Assert.False(doc.Root["list"].TryGetValue(1, out _));
        Assert.False(doc.Root["spec"].TryGetValue(0, out _));

        InvalidOperationException notMapping = Assert.Throws<InvalidOperationException>(() => doc.Root["list"]["x"]);
        Assert.Contains("'x'", notMapping.Message);
        Assert.Contains("sequence", notMapping.Message);
        ArgumentOutOfRangeException outside = Assert.Throws<ArgumentOutOfRangeException>(() => doc.Root["list"][4]);
        Assert.Contains("4", outside.Message);
        Assert.Contains("1 item", outside.Message);
    }

    [Fact]
    public void A_pointer_error_names_the_pointer_and_the_deepest_prefix_that_exists()
    {
        using YamlDocument doc = YamlDocument.Parse("spec:\n  replicas: 3\n  ports: [80, 81]\n");
        YamlException a = Assert.Throws<YamlException>(() => doc.Get<int>("/spec/nope"));
        Assert.Equal(YamlErrorCategory.PatchPath, a.Category);
        Assert.Contains("'/spec/nope'", a.Message);
        Assert.Contains("'/spec' exists but has no key 'nope'", a.Message);

        YamlException b = Assert.Throws<YamlException>(() => doc.Get<int>("/other/x"));
        Assert.Contains("the document root exists but has no key 'other'", b.Message);

        YamlException c = Assert.Throws<YamlException>(() => doc.GetNode("/spec/ports/5"));
        Assert.Contains("'/spec/ports' is a sequence of 2 items and has no item 5", c.Message);

        YamlException d = Assert.Throws<YamlException>(() => doc.GetNode("/spec/replicas/deeper"));
        Assert.Contains("'/spec/replicas' is a scalar", d.Message);

        YamlException e = Assert.Throws<YamlException>(() => doc.Set("/spec/replicas/deeper", 1));
        Assert.Contains("'/spec/replicas/deeper'", e.Message);
        Assert.Contains("scalar", e.Message);

        YamlException f = Assert.Throws<YamlException>(() => doc.Remove("/spec/gone"));
        Assert.Contains("'/spec' exists but has no key 'gone'", f.Message);
    }

    [Fact]
    public void A_patch_error_keeps_its_category_operation_and_atomicity_and_names_the_pointer()
    {
        const string text = "spec:\n  replicas: 3\n";
        using YamlDocument doc = YamlDocument.Parse(text);
        YamlException ex = Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"replace","path":"/spec/replicas","value":9},{"op":"remove","path":"/spec/nope"}]"""));
        Assert.Equal(YamlErrorCategory.PatchPath, ex.Category);
        Assert.Equal(1, ex.OperationIndex);
        Assert.Contains("'/spec/nope'", ex.Message);
        Assert.Contains("'/spec' exists but has no key 'nope'", ex.Message);
        Assert.Equal(text, doc.ToString());

        YamlException test = Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"test","path":"/spec/replicas","value":4}]"""));
        Assert.Equal(YamlErrorCategory.PatchTest, test.Category);
        Assert.Contains("'/spec/replicas'", test.Message);
        YamlException missing = Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"test","path":"/spec/zzz","value":4}]"""));
        Assert.Equal(YamlErrorCategory.PatchPath, missing.Category);
        Assert.Contains("'/spec/zzz'", missing.Message);
    }

    [Fact]
    public void A_pointer_that_would_match_if_a_slash_were_escaped_says_so()
    {
        using YamlDocument doc = YamlDocument.Parse("metadata:\n  labels:\n    app.kubernetes.io/name: billing\n    a/b/c: deep\n");
        YamlException ex = Assert.Throws<YamlException>(() => doc.Get<string>("/metadata/labels/app.kubernetes.io/name"));
        Assert.Equal(YamlErrorCategory.PatchPath, ex.Category);
        Assert.Contains("'app.kubernetes.io/name'", ex.Message);
        Assert.Contains("~1", ex.Message);
        Assert.Contains("'/metadata/labels/app.kubernetes.io~1name'", ex.Message);
        Assert.Equal("billing", doc.Get<string>("/metadata/labels/app.kubernetes.io~1name"));

        YamlException deeper = Assert.Throws<YamlException>(() => doc.Set("/metadata/labels/a/b/c", "x"));
        Assert.Contains("'/metadata/labels/a~1b~1c'", deeper.Message);

        YamlException plain = Assert.Throws<YamlException>(() => doc.Get<string>("/metadata/labels/zzz/yyy"));
        Assert.DoesNotContain("~1", plain.Message);
    }

    // ---- The document index ----

    [Fact]
    public void A_document_index_outside_the_stream_is_the_same_error_wherever_it_is_given()
    {
        using YamlDocument doc = YamlDocument.Parse("a: 1\n---\nb: 2\n");
        ArgumentOutOfRangeException list = Assert.Throws<ArgumentOutOfRangeException>(() => doc.Documents[5]);
        ArgumentOutOfRangeException set = Assert.Throws<ArgumentOutOfRangeException>(() => doc.Set("/a", 1, document: 5));
        ArgumentOutOfRangeException get = Assert.Throws<ArgumentOutOfRangeException>(() => doc.Get<int>("/a", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Add("/a", 1, document: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Remove("/a", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Contains("/a", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetNode("/a", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.TryGet("/a", out int _, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Documents[-1]);
        Assert.Equal(list.Message, set.Message);
        Assert.Equal(list.Message, get.Message);
        Assert.Contains("2 documents", list.Message);
        Assert.Contains("index 5", list.Message);
        Assert.Equal("document", set.ParamName);

        // Inside a patch, a bad index is a patch error like every other way a patch can fail.
        YamlException patch = Assert.Throws<YamlException>(() => doc.ApplyPatch("""[{"op":"add","path":"/x","value":1}]""", 5));
        Assert.Equal(YamlErrorCategory.PatchPath, patch.Category);
        Assert.Contains("no document at index 5", patch.Message);
        Assert.Equal(YamlErrorCategory.PatchPath, Assert.Throws<YamlException>(() => doc.ApplyMergePatch("{}", 5)).Category);
        Assert.Equal("a: 1\n---\nb: 2\n", doc.ToString());
    }

    [Fact]
    public void An_empty_stream_has_no_listed_document_but_takes_the_first_write()
    {
        using YamlDocument doc = YamlDocument.Parse(string.Empty);
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => doc.Documents[0]);
        Assert.Contains("0 documents", ex.Message);
        doc.Set("/a", 1);
        Assert.Equal("a: 1\n", doc.ToString());
    }

    // ---- Async members ----

    [Fact]
    public async Task A_document_parses_from_a_stream_and_writes_to_one_asynchronously()
    {
        byte[] input = "# lead\na: 1   # keep\nlist:\n  - x\n"u8.ToArray();
        using var source = new MemoryStream(input);
        using YamlDocument doc = await YamlDocument.ParseAsync(source, null, TestContext.Current.CancellationToken);
        doc.Set("/a", 2);
        using var sink = new MemoryStream();
        await doc.WriteToAsync(sink, TestContext.Current.CancellationToken);
        Assert.Equal("# lead\na: 2   # keep\nlist:\n  - x\n", System.Text.Encoding.UTF8.GetString(sink.ToArray()));

        using var plain = new MemoryStream();
        doc.WriteTo(plain);
        Assert.Equal(plain.ToArray(), sink.ToArray());
    }

    [Fact]
    public async Task Async_writing_keeps_the_encoding_and_honours_cancellation()
    {
        byte[] utf16 = new System.Text.UnicodeEncoding(false, true).GetPreamble().Concat(new System.Text.UnicodeEncoding(false, true).GetBytes("a: 1\n")).ToArray();
        using YamlDocument doc = YamlDocument.Parse(utf16);
        using var sink = new MemoryStream();
        await doc.WriteToAsync(sink, TestContext.Current.CancellationToken);
        Assert.Equal(utf16, sink.ToArray());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await doc.WriteToAsync(new MemoryStream(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await YamlDocument.ParseAsync(new MemoryStream(utf16), null, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await doc.WriteToAsync(null!, TestContext.Current.CancellationToken));
    }
}

internal static class DictionaryNodeExtensions
{
    public static YamlNode ToNode(this Dictionary<string, object> value) => YamlNode.Create(value);
}
