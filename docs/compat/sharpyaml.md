# OneYaml.Compat.SharpYaml: differences from SharpYaml 3

The package reproduces the documented behaviour and API of SharpYaml 3.14.0 for the serializer, never its defects.
Every difference below is checked against the real library by a differential test in `tests/OneYaml.Compat.Tests`.
Switching is a package swap and a `using` change: `using SharpYaml;` becomes `using OneYaml.Compat.SharpYaml;`
(and `SharpYaml.Serialization` becomes `OneYaml.Compat.SharpYaml.Serialization`).

Classes: **Intended** (a deliberate choice, kept), **Fix** (the real library is wrong; this package is right),
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

## Fixes (the real library is wrong)

| Difference | Repro |
|---|---|
| Output with `WriteIndented = false` is malformed YAML in the real library (it writes valid YAML at `IndentSize` 4); here it is valid | `Serialize(new { A = new { B = 1 } }, new() { WriteIndented = false })` |
| Empty or comment-only text read into a value type throws `NullReferenceException` in the real library; here it gives the default value, as a null member does | `Deserialize<int>("")` and `Deserialize<int>("# note")` give 0 |
| `Uri` cannot be read or written by the real library | `Deserialize<Uri>("http://x/y")` |
| The real library rejects about 70 valid YAML 1.2 documents (a bare document after `...`, `?x` as a plain scalar, anchor names containing `:`, and others) | yts `2EBW`-style cases; see `CorpusTests` |
| Block scalars that keep trailing lines lose or gain a line break in the real library | `\|+\n a\n\n` (`JEF9/02`, `L24T/01`) |
| Plain scalars that start or end in an indicator are misread by the real library | `652Z`, `4ABK`, `HM87/01` |
| A line that starts with `%` after the first line of a document, with another document following, is part of the first document. The real library fails on a block scalar that holds one | `Deserialize<string>("--- \|\nfoo\n%bar\n---\nx\n")` gives `"foo\n%bar\n"`; `"a\n%x\n%y\n---\nb\n"` gives `"a %x %y"` |
| A stream with bytes that are not valid in its encoding ends in a `SyntaxErrorException`; the real library throws a `DecoderFallbackException` (not a `YamlException`) for UTF-8 input, and for UTF-16 or UTF-32 input with a byte order mark it silently replaces the bad bytes with U+FFFD. The error mark counts characters of the detected encoding | `Deserialize<Plain>(stream)` with a stray `0xFF` byte |
| Aliases into `object` are refused by the real library unless `ReferenceHandling` is `Preserve`; here they are followed within the alias budget | `a: &x [1]\nb: *x` into `object` |

## Intended differences

| Difference | Repro |
|---|---|
| YAML 1.2 Core only: `1_000` and `0b11` are text, not numbers (the real library reads them as numbers) | `Deserialize<int>("1_000")` throws here (real: 1000) |
| A hexadecimal integer is accepted into a `double` member | `Deserialize<Holder>("Score: 0x10")` |
| A very large integer in an `object` read is a `BigInteger` (real: `double`) | `Deserialize<object>("123456789012345678901234567890")` |
| Whole doubles and halves are written with a fraction so they read back as floats, and an exponent form carries one too | `Serialize(100.0)` gives `100.0` (real: `100`); `Serialize(Half.Epsilon)` gives `6.0E-08` (real: `6E-08`) |
| 1.1 look-alikes are quoted on output so any reader sees a string | `Serialize("y")` gives `'y'`; also `'Y'`, `'on'` |
| Member order is base class first, fields before properties; `Sorted` order applies to members only | `Serialize(new Derived())` |
| Quoted `"5"` and `"true"` are read into numbers and booleans (kept from the real library's lenient reads) | `Deserialize<int>("\"5\"")` gives 5 |
| A number whose digits overflow the type is an error, quoted or not; the real library turns it into infinity, which changes the value. Plain `.inf` and `-.inf` still read as infinity (a quoted `".inf"` is refused; the real library reads it as infinity) | `Deserialize<double>("1e400")`, `Deserialize<float>("1e39")` and `Deserialize<Half>("1e10")` throw here (real: `∞`) |
| A stream in UTF-16 or UTF-32 without a byte order mark is detected from its first characters, as YAML 1.2 says (the real library fails) | `Deserialize<Plain>(stream)` over `a: b` in UTF-16 |
| A null read into a non-nullable value type is the default at the top level too, as it already is for a member (real: `YamlException` at the top level) | `Deserialize<int>("null")` gives 0 |
| Enums are written by name regardless of the naming policy | `Serialize(Mood.Happy, new() { PropertyNamingPolicy = CamelCase })` |
| `MaxDepth` defaults to 64 as in the real library and is never raised above the library limit | `YamlSerializerOptions { MaxDepth = 100000 }` is capped |
| Error marks are a point (`Start == End`), normally at the real library's line and column but not always | `Deserialize<object>("a: [")` |
| Syntax errors are `SyntaxErrorException`; every other category is `YamlException` (the real `SemanticErrorException` is never raised) | `Deserialize<object>("a: [")` is a syntax error; `Deserialize<int>("x")` is a `YamlException` |
| `Stream` input is read to the end, with UTF-8, UTF-16 and UTF-32 detected, and left open | `Deserialize<Plain>(stream)` |
| `BlockSequenceMappingStyle`, `BlockSequenceSequenceStyle`, `ScalarStylePreferences` are accepted and change nothing | |
| `GetTypeInfo` returns a nullable `YamlTypeInfo` | |
| Only the first document is read, as in the real library; the rest of the stream is not parsed unless the first fails | `Deserialize<Plain>("a: 1\n---\n[")` |

## Gaps

- `DuplicateKeyHandling.LastWins` and `FirstWins` (the engine has no option for them).
- Source-generated contexts (see "What is left out").
- `ReferenceHandling.Preserve` on write: shared objects are written out in full, and a cycle is a `YamlException`.
- Error text differs from the real library's wording; marks follow the rule above.
