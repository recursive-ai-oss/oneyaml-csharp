# Patches

`YamlDocument` applies a JSON Patch (RFC 6902) or a JSON Merge Patch (RFC 7396) to a YAML file and keeps its layout: every value the patch does not touch keeps its text, comments included, and every value it writes is placed and styled as an [edit](editing.md#how-a-new-value-is-written) would be. Paths are RFC 6901 pointers, read as in [Pointers](editing.md#pointers).

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

## The test operation

`test` compares the value in the document with the patch's value by the YAML data model under the Core schema, not as JSON text. A plain `yes` in the file equals the JSON string `"yes"`; a quoted `'1'` is a string and does not equal the number `1`; and the integer `1` does not equal the float `1.0`, because YAML 1.2 keeps integers and floats apart.

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
