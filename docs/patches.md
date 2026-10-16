# Patches

`YamlDocument` applies JSON Patch operations ([RFC 6902](https://www.rfc-editor.org/rfc/rfc6902)) with YAML value equality for `test`, and JSON Merge Patch ([RFC 7396](https://www.rfc-editor.org/rfc/rfc7396)), to a YAML file and keeps its layout: every value the patch does not touch keeps its text, comments included, and every value it writes is placed and styled as an [edit](editing.md#how-a-new-value-is-written) would be. Paths are RFC 6901 pointers, read as in [Pointers](editing.md#pointers). One rule differs from RFC 6902 on purpose: `test` compares YAML values, so `1` and `1.0` are different ([Differences from RFC 6902](#differences-from-rfc-6902)).

The samples on this page are complete programs that assume implicit usings (the default for new projects); the first shows the `using OneYaml;` line the others leave out.

## JSON Patch

<!-- sample: PatchJson -->
```csharp
using OneYaml;

using var doc = YamlDocument.Parse("""
    name: web          # the public site
    replicas: 2
    labels:
      tier: frontend
    """);

doc.ApplyPatch("""
    [
      { "op": "test",    "path": "/name",        "value": "web" },
      { "op": "replace", "path": "/replicas",    "value": 3 },
      { "op": "add",     "path": "/labels/team", "value": "core" }
    ]
    """);
Console.WriteLine(doc);
// name: web          # the public site
// replicas: 3
// labels:
//   tier: frontend
//   team: core
```

`ApplyPatch` takes the patch as JSON text, UTF-8 bytes, a `JsonNode`, or a `YamlPatch` built in code. All six operations are supported: `add`, `remove`, `replace`, `move`, `copy` and `test`. A value that is moved or copied is written in the style of its new place, so a comment on the line of a moved value does not move with it.

> **`test` compares YAML values, not JSON values.** An integer and a float are different values in YAML 1.2, so a `test` for `1` fails on `1.0` in the file, where RFC 6902 would pass it. This is on purpose: the patch checks the value a YAML reader sees. A patch made for a JSON form of the data may need its `test` values changed; see [Differences from RFC 6902](#differences-from-rfc-6902).

<!-- sample: PatchTestNumbers -->
```csharp
using var doc = YamlDocument.Parse("timeout: 1.0\n");
try
{
    doc.ApplyPatch("""[{ "op": "test", "path": "/timeout", "value": 1 }]""");
}
catch (YamlException e) when (e.Category == YamlErrorCategory.PatchTest)
{
    Console.WriteLine(e.Message); // The value at '/timeout' is not the value the test expects.
}

doc.ApplyPatch("""[{ "op": "test", "path": "/timeout", "value": 1.0 }]"""); // passes
```

## A patch is atomic

The operations run in order. If one fails, the document is left exactly as it was before the patch, and the `YamlException` says which operation failed in `OperationIndex` (0-based).

<!-- sample: PatchAtomic -->
```csharp
const string Src = "name: web\nreplicas: 2\n";
using var doc = YamlDocument.Parse(Src);
try
{
    doc.ApplyPatch("""
        [
          { "op": "replace", "path": "/replicas", "value": 5 },
          { "op": "test",    "path": "/name",     "value": "api" }
        ]
        """);
}
catch (YamlException e)
{
    Console.WriteLine($"{e.Category} at operation {e.OperationIndex}"); // PatchTest at operation 1
}

Console.WriteLine(doc.ToString() == Src); // True
```

| Category | Meaning |
|---|---|
| `PatchInvalid` | The patch is not valid JSON, or an operation is malformed (an unknown `op`, a missing `value`) |
| `PatchPath` | A `path` or `from` names nothing, or the `document` index is outside the stream |
| `PatchTest` | A `test` found a different value |
| `RefusedAlias` | The operation would write through an alias ([Anchors and aliases](editing.md#anchors-and-aliases)) |
| `RefusedAmbiguous` | A pointer matches more than one key (with `AllowDuplicateKeys`) |
| `Limit` | The patch or the values it reads exceed the document's [limits](limits.md) |

## Differences from RFC 6902

The six operations and the order they run in follow RFC 6902, and paths follow RFC 6901. `test` compares the value in the document with the patch's value by the YAML data model under the Core schema, after following aliases, not as JSON values:

| | RFC 6902 | `ApplyPatch` |
|---|---|---|
| `test` on numbers | numbers are equal when they are numerically equal: `1` equals `1.0` | an integer and a float are different values: `1` does not equal `1.0`. Values of one type compare by value: `0x1F` equals `31`, and `1e2` equals `100.0` |
| `test` on mapping keys | object member names are strings | keys compare as YAML values: a plain key `80` is an integer and does not match the member `"80"` of a test value; a quoted key `"80"` does |
| `test` on strings | JSON strings | the scalar as the Core schema reads it: plain `yes` equals `"yes"`, and quoted `'1'` is a string, so it does not equal `1` |
| What the patch applies to | one JSON document | one document of a YAML stream, the first unless you pass `document: n` ([Several documents](#several-documents)) |

Pointers are not affected: a token names a key by its text, so `/80` reaches the integer key `80` as it would reach the member `"80"` of a JSON object.

**A patch made for a JSON form of the same data**, for example by a tool that converted the file to JSON and diffed it, applies the same way except for its `test` operations on floats and on mappings with keys that are not strings. Check those first: a JSON writer may write a whole float as `1`, and that `test` then fails on the `1.0` in the file. The values such a patch writes are read as the JSON shows them, so a float written as `1` goes into the file as the integer `1`.

## Building a patch in code

`YamlPatch` builds the same operations with typed values. The values are converted when they are added, so one patch can be applied to any number of documents. `YamlPatch.Parse` reads one from JSON, and the patch is a read-only list of its operations.

<!-- sample: PatchBuilder -->
```csharp
var patch = new YamlPatch()
    .Test("/name", "web")
    .Replace("/replicas", 3)
    .Add("/labels/tier", "frontend");

using var doc = YamlDocument.Parse("name: web\nreplicas: 2\nlabels: {}\n");
doc.ApplyPatch(patch);
Console.Write(doc);
// name: web
// replicas: 3
// labels: {tier: frontend}
```

`Add`, `Replace` and `Test` take plain values (null, strings, booleans, numbers, dictionaries and sequences of these), or any type through the reflection overload with `YamlSerializerOptions` or the generated-metadata overload with a `YamlTypeInfo<T>`.

## Merge patches

`ApplyMergePatch` applies an RFC 7396 merge patch: an object merges member by member, `null` removes a member, and any other value, an array included, replaces what is there. Removals in a mapping run after its other members. Merging an object into an alias is refused, as any write through an alias is.

<!-- sample: PatchMerge -->
```csharp
using var doc = YamlDocument.Parse("""
    name: web
    labels:
      tier: frontend   # used by the load balancer
      legacy: true
    """);

doc.ApplyMergePatch("""{ "labels": { "legacy": null, "team": "core" }, "replicas": 3 }""");
Console.WriteLine(doc);
// name: web
// labels:
//   tier: frontend   # used by the load balancer
//   team: core
// replicas: 3
```

A merge patch is atomic too, and takes JSON text, UTF-8 bytes or a `JsonNode`.

## Several documents

A patch applies to one document of the stream, the first by default; pass `document: n` for another. An index outside the stream is a `PatchPath` error, as for any other path the patch cannot reach.
