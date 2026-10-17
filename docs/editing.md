# Editing

`YamlDocument` holds a whole YAML stream with its text, comments and layout. Writing it back unedited gives the input byte for byte, and an edit changes only the text of the value it touches. Use it for files that people also edit by hand, where the diff of a change must hold only that change.

The samples on this page are complete programs that assume implicit usings (the default for new projects); the first shows the `using OneYaml;` line the others leave out.

## Indexers

`doc.Root` is the root node of the first document. A `YamlNode` reads like `JsonNode`: `node["key"]` for a mapping, `node[0]` for a sequence, and assignment to change a value.

<!-- sample: EditIndexers -->
```csharp
using OneYaml;

using var doc = YamlDocument.Parse("""
    # Deployment for the web tier
    spec:
      replicas: 2        # raised by the release job
      image: web:1.4
      ports: [80, 443]
    """);

YamlNode spec = doc.Root["spec"];
spec["replicas"] = 3;              // replaces the value, keeps the comment
spec["paused"] = false;            // a new key goes at the end of its mapping
spec["ports"][2] = 8443;           // an index equal to the count appends
spec["image"].Remove();
Console.WriteLine(doc);
// # Deployment for the web tier
// spec:
//   replicas: 3        # raised by the release job
//   ports: [80, 443, 8443]
//   paused: false
```

### What an edit keeps, and what it does not

- **Untouched text is kept byte for byte:** comments, blank lines, indentation, key order, the quoting of other values, line endings (`\n` or `\r\n`). A comment on the same line as a replaced scalar stays after the new value.
- **Removing an entry** removes its line, with the comment on that line and the comment lines just above it.
- **Replacing a mapping or a sequence** replaces its whole text, and the comments inside it go with it. Change the leaves instead when the comments matter.
- **Comments do not move with values.** A value moved by a patch `move` is written in the style of its new place, without the comments of its old one.
- **There is no comment API.** Comments are kept, not modelled: you cannot read, add or move them.
- **Encoding.** `ToString()` gives text. `ToBytes()` and `WriteTo(...)` give the encoding and byte order mark the input had; use them to write the file back ([Input, output and lifetime](#input-output-and-lifetime)).
- **Plain or quoted** is judged by the YAML 1.2 Core schema, so a new string `yes` is written plain. If a YAML 1.1 reader also reads the file, set `QuoteYaml11Lookalikes` ([How a new value is written](#how-a-new-value-is-written)).

- **Values you can assign.** `string`, `bool`, the integer types, `double`, `decimal` and `null` convert to a `YamlNode` implicitly. For anything else, `YamlNode.Create(value)` or `node.Set(value)` takes a dictionary, a sequence, another node, or any value the serializer can write.
- **A view follows its node.** A `YamlNode` stays valid across edits made through it or anywhere else in the document, as a `JsonNode` does. A view of a node that was removed, or that was inside a value that was replaced, throws `InvalidOperationException`. Any use after the document is disposed throws `ObjectDisposedException`.
- **Misses.** Reading a key that is not there throws `KeyNotFoundException`; use `TryGetValue` or `ContainsKey` to look without throwing. A sequence index outside the items throws `ArgumentOutOfRangeException`, and using a key on a scalar or a sequence throws `InvalidOperationException`.

## Pointers

Every edit can also be made with an RFC 6901 JSON Pointer. A token names a mapping key by its text, so `/80` reaches the key `80`; a sequence item by its index; and `-` the place after the last item. In a key, `~1` stands for `/` and `~0` for `~`.

<!-- sample: EditPointers -->
```csharp
using var doc = YamlDocument.Parse("""
    metadata:
      annotations:
        example.com/owner: web-team
    spec:
      containers:
        - name: web
          image: web:1.4
    """);

doc.Set("/spec/containers/0/image", "web:1.5");
doc.Set("/metadata/annotations/example.com~1owner", "platform"); // ~1 stands for '/'
doc.Add("/spec/containers/-", new Dictionary<string, object?> { ["name"] = "proxy", ["image"] = "envoy:1.30" });

Console.WriteLine(doc.Get<string>("/spec/containers/1/name")); // proxy
Console.WriteLine(doc.Contains("/spec/volumes"));            // False
if (doc.TryGet("/spec/containers/0/image", out string? image))
{
    Console.WriteLine(image); // web:1.5
}

Console.WriteLine(doc);
// metadata:
//   annotations:
//     example.com/owner: platform
// spec:
//   containers:
//     - name: web
//       image: web:1.5
//     - name: proxy
//       image: envoy:1.30
```

| Method | What it does |
|---|---|
| `Set(path, value)` | Replaces the value, or adds a missing mapping key at the end of its mapping; `-` appends to a sequence |
| `Add(path, value)` | RFC 6902 `add`: a sequence index inserts before the item there |
| `Remove(path)` | Removes the entry or item |
| `Get<T>(path)`, `TryGet<T>(path, out value)` | Reads a scalar as a .NET value |
| `GetNode(path)` | Returns the node, to read or edit through its view |
| `Contains(path)` | Tells whether the pointer names a value |

A pointer that names nothing is a `YamlException` with category `PatchPath`, the category every pointer miss shares. `Set` and `Add` take any value the serializer can write: through reflection with the `Set<T>(path, value, options)` overload, or through generated metadata with `Set(path, value, typeInfo)` ([Source generation](serializer.md#source-generation-and-native-aot)). `Get(path, typeInfo)` and `YamlNode.Deserialize<T>()` read a whole subtree into a type.

## How a new value is written

A replaced scalar keeps its style when that style can hold the new value. When it cannot, the value is written in the nearest style that can: a plain scalar becomes single-quoted, then double-quoted; a single-quoted one becomes double-quoted. A block scalar keeps its `|` or `>` and gets the chomping indicator the new text needs. The value read back is always exactly the value you set.

<!-- sample: EditStyles -->
```csharp
using var doc = YamlDocument.Parse("""
    plain: one
    single: 'one'
    double: "one"
    literal: |
      one
    """);

doc.Set("/plain", "two words");        // still plain
doc.Set("/single", "it's");            // still single-quoted, the quote doubled
doc.Set("/double", "tab\there");       // still double-quoted, with an escape
doc.Set("/literal", "no final break"); // still literal, with the strip indicator
Console.Write(doc);
// plain: two words
// single: 'it''s'
// double: "tab\there"
// literal: |-
//   no final break

doc.Set("/plain", "a: b");             // plain cannot hold ': ', so it is quoted
doc.Set("/single", "two\nlines\n");    // single quotes cannot keep the break, so double quotes
Console.WriteLine(doc.Root["plain"].Style);  // SingleQuoted
Console.WriteLine(doc.Root["single"].Style); // DoubleQuoted
```

A new key or item is written like its siblings: in a flow collection (`[...]`, `{...}`) as flow, in a block collection as block, at the same indentation, with the line breaks the file already uses. A new mapping or sequence value is written in block style unless it goes inside a flow collection.

Whether a new string can be plain is judged by the YAML 1.2 Core schema, the schema this library reads with: `yes` stays plain because it is a string under YAML 1.2, while `1.10` is quoted because it would read as a number. If a YAML 1.1 reader also reads the file, set `QuoteYaml11Lookalikes`, and such strings are quoted as `YamlSerializer` quotes them.

<!-- sample: EditLookalikes -->
```csharp
const string Src = "enabled: x\nversion: x\n";

using var core = YamlDocument.Parse(Src);
core.Set("/enabled", "yes");      // a string under YAML 1.2, so it stays plain
core.Set("/version", "1.10");     // a number under YAML 1.2, so it is quoted
Console.Write(core);
// enabled: yes
// version: '1.10'

using var both = YamlDocument.Parse(Src, new YamlDocumentOptions { QuoteYaml11Lookalikes = true });
both.Set("/enabled", "yes");      // quoted, so a YAML 1.1 reader also sees a string
Console.WriteLine(both.Root["enabled"].Style); // SingleQuoted
```

## Anchors and aliases

Reading follows aliases: `Get`, the indexers and `Deserialize` see the anchored value. Writing through an alias is refused, because it would change every place that shares the anchor. Replacing the alias itself, or the anchored original, is fine.

An alias means the most recent definition of its name that is written before it and already complete. An alias inside the node its own anchor names (`&a [*a]`) would be a cycle, so opening such a document is a `Syntax` error rather than a silent use of an older `&a`.

<!-- sample: EditAliases -->
```csharp
using var doc = YamlDocument.Parse("""
    defaults: &defaults
      cpu: 2
    prod: *defaults
    """);

Console.WriteLine(doc.Get<int>("/prod/cpu")); // 2

try
{
    doc.Set("/prod/cpu", 4); // would change 'defaults' too
}
catch (YamlException e) when (e.Category == YamlErrorCategory.RefusedAlias)
{
    Console.WriteLine(e.Message);
    // The edit would write through an alias and change every place that shares the anchored node.
}

doc.Set("/prod", new Dictionary<string, object?> { ["cpu"] = 4 }); // replacing the alias itself is fine
Console.WriteLine(doc);
// defaults: &defaults
//   cpu: 2
// prod:
//   cpu: 4
```

Removing a node whose anchor is still used by an alias is refused too. `YamlNode.Anchor`, `Tag` (resolved against `%TAG` directives) and `RawTag` (as written) tell you what a node carries, and a replaced scalar keeps its tag. A view reached through an alias is a read-only snapshot that is valid until the next edit; take it again from the root after editing. Reading through aliases counts against the alias budget of the document's [limits](limits.md).

## Reading while you edit

<!-- sample: EditReading -->
```csharp
using var doc = YamlDocument.Parse("""
    name: web
    replicas: 2
    labels: {tier: frontend, team: core}
    """);

YamlNode root = doc.Root;
Console.WriteLine(root["replicas"].GetValue<int>() + 1); // 3
Console.WriteLine(root["labels"].Style);                  // Flow

foreach (var (key, value) in root["labels"].Entries)
{
    Console.WriteLine($"{key.GetValue<string>()} = {value.GetValue<string>()}");
    // tier = frontend
    // team = core
}

if (!root.TryGetValue("owner", out YamlNode? owner))
{
    Console.WriteLine("no owner"); // no owner
}

var service = root.Deserialize<Service>()!;
Console.WriteLine($"{service.Name}: {service.Labels.Count} labels"); // web: 2 labels

record Service(string Name, int Replicas, Dictionary<string, string> Labels);
```

`Kind`, `Style`, `Count`, `Position` (line, column and byte offset), `IsNull`, `Items` and `Entries` describe a node; `GetValue<T>` and `TryGetValue<T>` read a scalar. `Deserialize<T>` binds a node as `YamlSerializer` would bind the same text, with the document's limits and source name. Unlike the serializer, a document keeps tags and accepts keys that are mappings or sequences.

## Repeated keys

A key that appears twice in a mapping is a `DuplicateKey` error when the document is parsed. To edit such a file anyway, set `AllowDuplicateKeys`; a pointer or indexer that then matches more than one key is refused with `RefusedAmbiguous`. Keys are compared after Core schema resolution, so `1` and `0x1` are the same key, but `1` and `"1"` are not.

## Several documents

A stream can hold several documents. `Documents` lists their roots, `Root` is the first one, and every pointer method and patch takes a `document` index, 0 by default. Output always writes the whole stream.

<!-- sample: EditStream -->
```csharp
using var doc = YamlDocument.Parse("""
    kind: Service
    name: web
    ---
    kind: Deployment
    name: web
    replicas: 2
    """);

Console.WriteLine(doc.Documents.Count); // 2
doc.Set("/replicas", 3, document: 1);
doc.Documents[0]["name"] = "web-svc";
Console.WriteLine(doc);
// kind: Service
// name: web-svc
// ---
// kind: Deployment
// name: web
// replicas: 3
```

A stream with no document (empty, or only comments) has `Documents.Count == 0`; `Root` then reads as null, and the first write creates the document.

## Recipe: update a Kubernetes manifest

A deploy job changes a manifest that people also maintain by hand. The file holds a Service and a Deployment; the job raises the replica count, moves to a new image, turns off coloured logs and drops an environment variable nobody uses any more. The input stands for a file that came from outside the program, so it is read with `YamlLimits.Untrusted` ([Limits](limits.md)). Kubernetes tools read manifests with a YAML 1.1 parser, where a plain `no` is the boolean false, so `QuoteYaml11Lookalikes` is on. The file is written back with `WriteTo`, which keeps its encoding.

<!-- sample: EditManifest -->
```csharp
byte[] input = """
    # Service for the web tier
    apiVersion: v1
    kind: Service
    metadata:
      name: web
    ---
    apiVersion: apps/v1
    kind: Deployment
    metadata:
      name: web
    spec:
      replicas: 2              # scaled by the release job
      template:
        spec:
          containers:
            - name: web
              image: web:1.4
              env:
                # left over from the old proxy
                - name: PROXY_URL
                  value: http://proxy:3128
                - name: LOG_COLOR
                  value: auto
    """u8.ToArray(); // in a real job: File.ReadAllBytes(path)

var options = new YamlDocumentOptions
{
    Limits = YamlLimits.Untrusted, // also bounds input size, scalar size and document count
    QuoteYaml11Lookalikes = true,  // so a YAML 1.1 reader sees 'no' as a string
};
using var doc = YamlDocument.Parse(input, options);

// Find the Deployment by its kind, not by its place in the file.
int deploy = Enumerable.Range(0, doc.Documents.Count).First(i => doc.Get<string>("/kind", i) == "Deployment");
const string Web = "/spec/template/spec/containers/0";
doc.Set("/spec/replicas", 3, deploy);
doc.Set($"{Web}/image", "web:1.5", deploy);
doc.Set($"{Web}/env/1/value", "no", deploy);
doc.Remove($"{Web}/env/0", deploy); // the comment line above it goes too

using var file = new MemoryStream(); // in a real job: File.Create(path)
doc.WriteTo(file);                   // in the encoding the input had
file.Position = 0;
Console.Write(new StreamReader(file).ReadToEnd());
// # Service for the web tier
// apiVersion: v1
// kind: Service
// metadata:
//   name: web
// ---
// apiVersion: apps/v1
// kind: Deployment
// metadata:
//   name: web
// spec:
//   replicas: 3              # scaled by the release job
//   template:
//     spec:
//       containers:
//         - name: web
//           image: web:1.5
//           env:
//             - name: LOG_COLOR
//               value: 'no'
```

The diff of the file holds only the job's changes: the replica count and the image, each in place with its comment kept, the quoted `'no'`, and the removed variable with the comment line that described it. The Service is untouched. Without `QuoteYaml11Lookalikes` the value would be written as a plain `no`, which is a string under YAML 1.2 but false for a YAML 1.1 reader. To change many files, catch `YamlException` for each one: a file over the limits, or one where the Deployment has no such path, then fails on its own and nothing is written for it.

## Errors

A failed edit throws and leaves the document exactly as it was. If even putting the document back fails, for example because memory runs out while it is restored, the edit's own exception still reaches you, and from then on the document refuses every use with `InvalidOperationException`; `Dispose` still works. Problems with the YAML, the pointer or the value are a `YamlException` with a category: `PatchPath` (the pointer names nothing), `RefusedAlias`, `RefusedAmbiguous`, or `Limit`. Mistakes in how the API is used are the usual .NET exceptions listed above (`KeyNotFoundException`, `ArgumentOutOfRangeException`, `InvalidOperationException`, `ObjectDisposedException`), and a value with no YAML form is an `ArgumentException`.

## Input, output and lifetime

- `Parse` takes a `string`, a `ReadOnlySpan<char>`, bytes in UTF-8, UTF-16 or UTF-32 (detected from a byte order mark or the first characters), or a `Stream` (also `ParseAsync`). A seekable stream longer than `MaxInputBytes` is refused before it is read.
- `ToString()` gives the text; `ToUtf8Bytes()` UTF-8; `ToBytes()`, `WriteTo(...)` and `WriteToAsync(...)` the encoding and byte order mark the input had.
- `WriteTo(IBufferWriter<byte>)` and `WriteTo(Stream)` write into a buffer you own and can reuse, such as an `ArrayBufferWriter<byte>` cleared with `ResetWrittenCount()` between calls. For a document read as UTF-8 they copy its text straight into that buffer, while `ToUtf8Bytes()` and `ToBytes()` allocate a new array on every call; this matters when you write large documents again and again. A UTF-16 or UTF-32 document is converted first, so writing it allocates either way, and `WriteToAsync` copies the text into a new array before it awaits the write.
- `YamlDocumentOptions` sets `Limits`, `AllowDuplicateKeys`, `QuoteYaml11Lookalikes` and `SourceName` (the name that starts every error message).
- A document holds pooled memory; dispose it when you are done. It is not thread-safe.
- To apply a JSON Patch or a merge patch, see [Patches](patches.md).
