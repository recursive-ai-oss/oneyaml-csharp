# Limits

YAML can describe far more work than its size suggests: an alias can stand for a whole subtree, so a few hundred bytes can expand to a billion values, and deep nesting can exhaust the stack. `YamlLimits` bounds that work. Exceeding a limit throws a `YamlException` with category `Limit`, and the message names the limit and its value.

The samples on this page are complete programs that assume implicit usings (the default for new projects); the first shows the `using OneYaml;` line the others leave out.

## The presets

| Limit | `YamlLimits.Default` | `YamlLimits.Untrusted` |
|---|---|---|
| `MaxDepth`: nesting of collections | 1,000 | 128 |
| Alias expansion, nodes (`AliasNodeFactor`, `AliasNodeFloor`) | 10 × the document's own nodes, at least 1,000,000 | 2 × the document's own nodes, at least 100,000 |
| Alias expansion, bytes (`AliasByteFactor`, `AliasByteFloor`) | 10 × the input size, at least 64 MiB | 2 × the input size, at least 8 MiB |
| `MaxIntegerDigits`: digits of an integer read as a number of any size | 4,300 | 4,300 |
| `MaxInputBytes` | none | 64 MiB |
| `MaxScalarBytes` | none | 16 MiB |
| `MaxDocuments`: documents per stream | none | 10,000 |

`Default` applies whenever you pass no limits. It bounds the work that grows faster than the input (depth, alias expansion, long integers) and is meant never to get in the way of a real file. It sets no cap on input size, scalar size or the number of documents: those are capped only when you set `MaxInputBytes`, `MaxScalarBytes` and `MaxDocuments`, which `Untrusted` does, and `Untrusted` also tightens the default bounds. `YamlLimits.None` removes every bound you can set, alias expansion included; use it only for input you fully trust. Under any limits, a stream or reader too large for one buffer is still refused ([Input size](#input-size)).

Integers that fit in 64 bits are not affected by `MaxIntegerDigits`; the bound exists because converting a very long integer to a `BigInteger` takes more than linear time.

## Input you do not control

Pass `YamlLimits.Untrusted` for anything that comes from outside your program: uploads, request bodies, files from users or other systems.

<!-- sample: LimitsUntrusted -->
```csharp
using OneYaml;

// Nine levels of ten aliases each: a few hundred bytes that expand to a billion strings.
var bomb = new StringBuilder("a: &a [x, x, x, x, x, x, x, x, x, x]\n");
for (char c = 'b'; c <= 'i'; c++)
{
    string prev = $"*{(char)(c - 1)}";
    bomb.Append($"{c}: &{c} [{string.Join(", ", Enumerable.Repeat(prev, 10))}]\n");
}

var options = new YamlSerializerOptions { Limits = YamlLimits.Untrusted };
try
{
    YamlSerializer.Deserialize<object>(bomb.ToString(), options);
}
catch (YamlException e) when (e.Category == YamlErrorCategory.Limit)
{
    Console.WriteLine(e.Message);
    // Expanding the aliases exceeds the budget set by YamlLimits (AliasNodeFactor 2, AliasNodeFloor 100000, AliasByteFactor 2, AliasByteFloor 8388608). (line 5, column 36)
}
```

(`StringBuilder` needs `using System.Text;`.) The same limits go to a document through `YamlDocumentOptions`, and they hold for everything that follows aliases there: the indexers, `Get`, `Deserialize`, and the `test` and `copy` operations of a patch.

<!-- sample: LimitsDepth -->
```csharp
string deep = new string('[', 200) + new string(']', 200);
try
{
    YamlSerializer.Deserialize<object>(deep, new YamlSerializerOptions { Limits = YamlLimits.Untrusted });
}
catch (YamlException e)
{
    Console.WriteLine(e.Message); // The nesting depth is larger than the limit MaxDepth (128). (line 1, column 129)
}
```

The depth is checked before any binder or converter frame runs, so the depth of the call stack is bounded too. A stack overflow ends a .NET process and cannot be caught, which is why the default depth is 1,000 and not higher.

## Changing one bound

`YamlLimits` is a record: start from a preset and change what your input needs with `with`.

<!-- sample: LimitsCustom -->
```csharp
// Start from the preset and change only what your input needs.
YamlLimits limits = YamlLimits.Untrusted with { MaxInputBytes = 1024 * 1024, MaxDocuments = 1 };

try
{
    using var doc = YamlDocument.Parse(new string('#', 2 * 1024 * 1024), new YamlDocumentOptions { Limits = limits });
}
catch (YamlException e)
{
    Console.WriteLine(e.Category); // Limit
}
```

## Input size

`MaxInputBytes` is checked against the input as given and again against its UTF-8 form. `YamlDocument.Parse(Stream)` refuses a seekable stream that is longer than the limit before reading it, and stops reading any other stream as soon as it passes the limit. `YamlSerializer.Deserialize` and `DeserializeAll` over a `TextReader` count the text in UTF-8 bytes while they read it and stop with the `Limit` error after at most 4,096 characters past the limit, so a very large or endless reader does not fill memory first. A `StringReader` is taken whole, since its text is already in memory, and counted as a string is. The compatibility adapters' `TextReader` overloads, and the SharpYaml adapter's `Stream` overloads, also stop reading once the text is over the limit. The SharpYaml adapter reads only the first document, as SharpYaml does, but holds the whole text, every document included, to `MaxInputBytes`.

With no `MaxInputBytes` set, a stream or a reader other than a `StringReader` is still read in pieces, to its end. A stream or reader too large for one buffer, that is more than the runtime can keep in one array or string, ends in the `Limit` error as well (in the adapters, the exception type each one uses for a limit), not in an out-of-memory or I/O error.

## The compatibility adapters

The adapters read with `YamlLimits.Default` unless you give them limits. Neither YamlDotNet nor SharpYaml has these settings; the adapters add them so that outside input can be read through them too:

| Adapter | Where the limits go |
|---|---|
| `OneYaml.Compat.YamlDotNet` | `DeserializerBuilder.WithLimits(YamlLimits)`, `YamlStream.Load(TextReader, YamlLimits)`, `new Parser(TextReader, YamlLimits)` |
| `OneYaml.Compat.SharpYaml` | `YamlSerializerOptions.Limits` (`null` means `YamlLimits.Default`) |

Pass `YamlLimits.Untrusted` for input you do not control. The adapters' own depth settings, `WithMaximumRecursion` and `MaxDepth`, can lower the depth further, never raise it above these limits. In a file that imports an adapter namespace, import the type with `using YamlLimits = OneYaml.YamlLimits;` rather than `using OneYaml;`: the `OneYaml` namespace has types with the same names as the adapters' (`YamlSerializer`, `YamlSerializerOptions`, `YamlException`, `YamlDocument`, `YamlNode`), and importing both makes those names ambiguous (error CS0104). The migration guides show each one ([from YamlDotNet](migrate-yamldotnet.md#input-you-do-not-control), [from SharpYaml](migrate-sharpyaml.md#input-you-do-not-control)).

## What the limits do not cover

- **Your converters.** Code in a `YamlConverter<T>` runs outside these bounds. A converter that recurses for each level of nesting should call `RuntimeHelpers.EnsureSufficientExecutionStack`, especially under `YamlLimits.None`.
- **The compatibility adapters without limits given.** They read with `YamlLimits.Default`, which does not cap input size, scalar size or documents ([The compatibility adapters](#the-compatibility-adapters)).
- **Memory without `Untrusted`.** Under `Default` there is no cap on input size, scalar size or documents per stream, so memory grows with the input. A `YamlDocument` keeps the whole text and an index of it.
- **What you write.** `Serialize` writes a value that is shared in several places once for each place, so its output can be far larger than the object graph in memory.

See [Security](../SECURITY.md) for what the library promises and how to report a problem.
