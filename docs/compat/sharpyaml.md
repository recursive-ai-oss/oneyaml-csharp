# OneYaml.Compat.SharpYaml: differences from SharpYaml 3.14.0

The package covers a subset of SharpYaml's API: the common serializer paths (`YamlSerializer`, `YamlSerializerOptions`,
the attributes and the exceptions), listed in [What is covered](#what-is-covered). It reproduces the documented behaviour of
SharpYaml for those paths, never its defects. It is tested against SharpYaml 3.14.0: every difference below is checked
against that version by a differential test in `tests/OneYaml.Compat.Tests`. Newer SharpYaml releases are not tested.

Not covered, so code that uses it does not build:

- custom converters, the `IYamlOn*` hooks, polymorphism and extension data;
- generated contexts: a `YamlSerializerContext` compiles, but nothing is generated for it;
- the `Model` and `Syntax` namespaces, schemas, the scanner, parser and emitter;
- the rest of [What is left out](#what-is-left-out), which also lists the options that compile but fail at first use.

The adapter's types are its own, in the `OneYaml.Compat.SharpYaml` namespaces; they are not SharpYaml's types. Code you
control switches with a package swap and a `using` change: `using SharpYaml;` becomes `using OneYaml.Compat.SharpYaml;`
(and `SharpYaml.Serialization` becomes `OneYaml.Compat.SharpYaml.Serialization`). A third-party library that takes or
returns SharpYaml types (options, a type info, a context) still needs SharpYaml itself and cannot be handed the adapter's
types.

For the steps of a migration, what to check after the swap, and the move to the `OneYaml` API, see
[Migrating from SharpYaml](../migrate-sharpyaml.md). The adapter reads with the library's default limits
unless `YamlSerializerOptions.Limits` is set; that option is an addition, not in SharpYaml, and `YamlLimits.Untrusted` is the
setting for input you do not control ([Limits](../limits.md#the-compatibility-adapters)).

Classes: **Intended** (a deliberate choice, kept), **Fix** (SharpYaml 3.14.0 writes output that does not read back, or gives a result the YAML does not say; the package does not reproduce it),
**Gap** (a behaviour the package does not reproduce yet).

## What is covered

- `YamlSerializer.Serialize`, `Deserialize` and `TryDeserialize` over `string`, `ReadOnlySpan<char>`, `TextReader`,
  `Stream`, `TextWriter` and `IBufferWriter<char>`, each with `Type`/generic, options, context and `YamlTypeInfo` forms.
- `YamlSerializerOptions` and the enums, `ReflectionYamlTypeInfoResolver`, `YamlTypeInfo`/`YamlTypeInfo<T>`.
- Attributes: `YamlPropertyName`, `YamlIgnore`, `YamlInclude`, `YamlRequired`, `YamlPropertyOrder`, `YamlConstructor`,
  `YamlBlockSequenceItemStyle`, `YamlSerializable` (accepted so code compiles); `System.Text.Json` attributes are the fallback.
- `YamlException`, `SyntaxErrorException`, `SemanticErrorException`, `Mark`.
- Constructors, records, init-only and required members, enums, nullable types, collections, dictionaries, `object`.

## What is left out

A program that uses these fails to compile (so the gap is visible), except the options listed under "Fail at first use".

- Custom converters: `YamlConverter<T>`, `YamlReader`, `YamlWriter`, `YamlSerializerOptions.Converters`, `[YamlConverter]`.
- Serialization hooks (`IYamlOnSerializing` and the other `IYamlOn*` interfaces).
- Polymorphism: `[YamlPolymorphic]`, `[YamlDerivedType]`, `PolymorphismOptions`. Extension data: `[YamlExtensionData]`.
- The source-generated context shape: `[YamlSerializable] partial class Ctx : YamlSerializerContext` compiles, but there is no
  generated `Ctx.Default` or typed property. Use the reflection path, or `OneYaml`'s own generator.
- `Model.*` (`YamlStream`, `YamlDocument`, nodes), `Syntax.*`, `Schemas`, scanner, parser, emitter.

Fail at first use with `NotSupportedException` (naming the option): `Schema`/`UseSchema` other than Core,
`DuplicateKeyHandling` other than the default error, `DictionaryKeyPolicy`, `PreferredObjectCreationHandling.Populate`,
`DefaultIgnoreCondition` of `Always`/`WhenWriting`/`WhenReading`, `UnsafeAllowDeserializeFromTagTypeName`, and a custom `TypeInfoResolver`.
The writer has one layout, so the layout options are refused the same way unless they ask for that layout:
`BlockSequenceMappingStyle` other than `Default` or `Compact`, `BlockSequenceSequenceStyle` other than `Default` or `Expanded`,
`ScalarStylePreferences` with `PreferPlainStyle` or `PreferQuotedForAmbiguousScalars` set, and `WriteIndented = false`. `ReferenceHandling` other than
`None` is refused when writing, because the writer writes a shared object in full each time and writes no anchors; reading
follows aliases with any value.

## Added, not in SharpYaml

- `YamlSerializerOptions.Limits` (`OneYaml.YamlLimits?`): the limits every read through the options keeps to. `null`, the
  default, means `YamlLimits.Default`; set `YamlLimits.Untrusted` for input you do not control. `MaxDepth` can lower the
  depth further, never raise it. Code that sets it does not compile against SharpYaml. Import the type with
  `using YamlLimits = OneYaml.YamlLimits;`, not `using OneYaml;`: that namespace has its own `YamlSerializer`,
  `YamlSerializerOptions` and `YamlException`, so importing it beside `OneYaml.Compat.SharpYaml` makes those names ambiguous (error CS0104).

## Fixes

| Difference | Repro |
|---|---|
| Empty or comment-only text read into a value type throws `NullReferenceException` in the real library; here it gives the default value, as a null member does | `Deserialize<int>("")` and `Deserialize<int>("# note")` give 0 |
| `Uri` cannot be read or written by the real library | `Deserialize<Uri>("http://x/y")` |
| The real library rejects about 70 valid YAML 1.2 documents (a bare document after `...`, `?x` as a plain scalar, anchor names containing `:`, and others) | yts `2EBW`-style cases; see `CorpusTests` |
| Block scalars that keep trailing lines lose or gain a line break in the real library | `\|+\n a\n\n` (`JEF9/02`, `L24T/01`) |
| Plain scalars that start or end in an indicator are misread by the real library | `652Z`, `4ABK`, `HM87/01` |
| A line that starts with `%` after the first line of a document, with another document following, is part of the first document. The real library fails on a block scalar that holds one | `Deserialize<string>("--- \|\nfoo\n%bar\n---\nx\n")` gives `"foo\n%bar\n"`; `"a\n%x\n%y\n---\nb\n"` gives `"a %x %y"` |
| A stream with bytes that are not valid in its encoding ends in a `SyntaxErrorException`; the real library throws a `DecoderFallbackException` (not a `YamlException`) for UTF-8 input, and for UTF-16 or UTF-32 input with a byte order mark it silently replaces the bad bytes with U+FFFD. The error mark counts characters of the detected encoding | `Deserialize<Plain>(stream)` with a stray `0xFF` byte |
| Aliases into `object` are refused by the real library unless `ReferenceHandling` is `Preserve`; here they are followed within the alias budget | `a: &x [1]\nb: *x` into `object` |

One `%` case is kept as SharpYaml 3 has it, not as the YAML specification says. A `%` line straight after a block collection, before the next `---`, is taken as a directive of the next document, so `Deserialize<Dictionary<string, int>>("a: 1\n%TAG ! tag:example.com,2000:\n---\nb: 2\n")` gives `{a: 1}` in both. The specification requires a `...` document end marker before a directive, and the `OneYaml` API refuses this text without one. The adapter keeps SharpYaml's reading because it changes no data.

## Intended differences

| Difference | Repro |
|---|---|
| YAML 1.2 Core only: `1_000` and `0b11` are text, not numbers (the real library reads them as numbers) | `Deserialize<int>("1_000")` throws here (real: 1000) |
| A hexadecimal integer is accepted into a `double` member, and into `Int128`, `UInt128` and `Half` (the real library refuses it for these three; YAML 1.2 reads `0x10` as an integer, as for `long`) | `Deserialize<Holder>("Score: 0x10")`; `Deserialize<Int128>("0x10")` |
| A very large integer in an `object` read is a `BigInteger` (real: `double`) | `Deserialize<object>("123456789012345678901234567890")` |
| `Half` infinity and NaN are written as `.inf`, `-.inf` and `.nan`, the YAML 1.2 floats that `double` gets in both libraries (the real library writes `Infinity`, `-Infinity` and `NaN`, plain strings to any other reader), and `.inf`, `-.inf` and `.nan` are read into `Half` (the real library refuses them). The spellings the real library reads, `Infinity`, `-Infinity` and `NaN` in any letter case and with a sign, are read into `double`, `float` and `Half`, quoted or not | `Serialize(Half.PositiveInfinity)` gives `.inf\n`; `Deserialize<Half>(".nan")` is NaN |
| Whole doubles and halves are written with a fraction so they read back as floats, and an exponent form carries one too | `Serialize(100.0)` gives `100.0` (real: `100`); `Serialize(Half.Epsilon)` gives `6.0E-08` (real: `6E-08`) |
| 1.1 look-alikes are quoted on output so any reader sees a string | `Serialize("y")` gives `'y'`; also `'Y'`, `'on'` |
| Member order is base class first, fields before properties; `Sorted` order applies to members only | `Serialize(new Derived())` |
| Quoted `"5"` and `"true"` are read into numbers and booleans (kept from the real library's lenient reads) | `Deserialize<int>("\"5\"")` gives 5 |
| A number whose digits overflow the type is an error, quoted or not; the real library turns it into infinity, which changes the value. Plain `.inf` and `-.inf` still read as infinity (a quoted `".inf"` is refused; the real library reads it as infinity) | `Deserialize<double>("1e400")`, `Deserialize<float>("1e39")` and `Deserialize<Half>("1e10")` throw here (real: `∞`) |
| A stream in UTF-16 or UTF-32 without a byte order mark is detected from its first characters, as YAML 1.2 says (the real library fails) | `Deserialize<Plain>(stream)` over `a: b` in UTF-16 |
| A null inside a mapping or sequence, read into a non-nullable number or boolean, is the default value (real: `YamlException` for a mapping value or sequence item). A null that is the whole document is an error here too, as in the real library | `Deserialize<List<int>>("[~]")` gives `[0]`; `Deserialize<int>("~")` throws |
| Enums are written by name regardless of the naming policy | `Serialize(Mood.Happy, new() { PropertyNamingPolicy = CamelCase })` |
| `MaxDepth` defaults to 64 as in the real library and is never raised above the depth limit of `Limits`; the property reads back the value set, and the lower of the two applies | `YamlSerializerOptions { MaxDepth = 100000 }` reads back 100000 and still stops at the limit |
| Error marks are a point (`Start == End`), normally at the real library's line and column but not always | `Deserialize<object>("a: [")` |
| A leading byte order mark (U+FEFF) is never counted in error marks, so a text gets the marks it would have without the mark from a string, a span, a `TextReader` or a `Stream`. The real library counts the mark as a character and a column when the text is a string or a `TextReader`, but not when it reads a `Stream`, so the same text gets different marks from different inputs | `Deserialize<object>("\uFEFFa: 1\nb: [x\n")`: `Index` 12 in the real library, 11 here, as for the same text read from a stream |
| Syntax errors are `SyntaxErrorException`; every other category is `YamlException` (the real `SemanticErrorException` is never raised) | `Deserialize<object>("a: [")` is a syntax error; `Deserialize<int>("x")` is a `YamlException` |
| `Stream` input is read to the end, or until it passes `Limits.MaxInputBytes`, with UTF-8, UTF-16 and UTF-32 detected, and left open | `Deserialize<Plain>(stream)` |
| `BlockSequenceMappingStyle`, `BlockSequenceSequenceStyle`, `ScalarStylePreferences` and `WriteIndented` are accepted at their default values and refused at any value that asks for another layout | `Serialize(x, new() { BlockSequenceMappingStyle = YamlSequenceItemStyle.Expanded })` throws `NotSupportedException` |
| `GetTypeInfo` returns a nullable `YamlTypeInfo` | |
| Only the first document is read, as in the real library; the rest of the stream is not parsed unless the first fails. `Limits.MaxInputBytes` still applies to the whole text, every document included | `Deserialize<Plain>("a: 1\n---\n[")` |

## Gaps

- `DuplicateKeyHandling.LastWins` and `FirstWins` (the engine has no option for them).
- Source-generated contexts (see "What is left out").
- `ReferenceHandling.Preserve` on write: refused with `NotSupportedException` (see "Fail at first use"), as the writer writes no anchors.
- Error text differs from the real library's wording; marks follow the rule above.
