# OneYaml.Compat.YamlDotNet: differences from YamlDotNet 18.1.0

The package covers a subset of YamlDotNet's API: the common paths through the serializer builders (`SerializerBuilder`,
`DeserializerBuilder`, naming conventions, `IYamlTypeConverter`, the attributes) and the representation model (`YamlStream`
and its nodes), with the parser events they use. It reproduces the documented behaviour of YamlDotNet for those paths,
never its defects. It is tested against YamlDotNet 18.1.0: every difference below is checked against that version by a
differential test in `tests/OneYaml.Compat.Tests`. Newer YamlDotNet releases are not tested.

Not covered, so code that uses it does not build:

- static contexts (`StaticSerializerBuilder`) and other generated serializer code;
- type inspectors, node deserializers, object factories, event emitters and object-graph visitors;
- the scanner, `MergingParser` and comment events;
- the rest of the [surface not provided](#surface-not-provided-gap).

The adapter's types are its own, in the `OneYaml.Compat.YamlDotNet` namespaces; they are not YamlDotNet's types. Code you
control switches with a package swap and a `using` change: `using YamlDotNet.Serialization;` becomes
`using OneYaml.Compat.YamlDotNet.Serialization;` (and likewise for `RepresentationModel` and `Core`). A third-party
library that takes or returns YamlDotNet types (a converter, a node, a builder) still needs YamlDotNet itself and cannot be
handed the adapter's types.

For the steps of a migration, what to check after the swap, and the move to the `OneYaml` API, see
[Migrating from YamlDotNet](../migrate-yamldotnet.md). The adapter reads with the library's default limits
unless you pass limits to `DeserializerBuilder.WithLimits`, `YamlStream.Load` or `new Parser`; those members are additions,
not in YamlDotNet, and `YamlLimits.Untrusted` is the setting for input you do not control ([Limits](../limits.md#the-compatibility-adapters)).
Import the type with `using YamlLimits = OneYaml.YamlLimits;`, not `using OneYaml;`: that namespace has its own `YamlException`,
`YamlDocument`, `YamlNode` and `YamlIgnoreAttribute`, so importing it beside the adapter's namespaces makes those names ambiguous (error CS0104).

Each class has a minimal repro. Label: **intended (spec)** = the YAML 1.2 text or a safer behaviour is kept on purpose;
**intended (bug not reproduced)** = YamlDotNet misbehaves and the package does not copy it; **gap** = documented API or
behaviour the package does not provide.

## Output (Serialize / YamlStream.Save)

| # | Difference | Repro | Label |
|---|---|---|---|
| O1 | Strings YAML 1.1 reads as non-strings are quoted | `Serialize("true")` → YamlDotNet `true`, package `'true'`; also `y`, `Y`, `yes`, `123`, `~` (the empty string is not a difference: both write `''`) | intended (spec: a reader of the output gets the string back) |
| O2 | Non-BMP characters written raw | `Serialize("\U0001F600")` → YamlDotNet `"\U0001F600"`, package the character | intended (spec) |
| O3 | Tab inside a double-quoted scalar written as `\t` | `Serialize("a\tb")` in double-quoted style | intended (spec; YamlDotNet writes a raw tab) |
| O3a | A plain scalar with a tab is quoted | `Serialize("a\tb")` | intended (spec; a tab in a plain scalar does not read back the same in every reader) |
| O3b | U+0085 and U+2028/U+2029 are written escaped | `Serialize("a\u2028b")` → YamlDotNet folds the character into a line break and the string changes on reading; the package writes `\L` style escapes | intended (bug not reproduced: data loss) |
| O3c | U+FEFF is written escaped | `Serialize("\uFEFFa")` → YamlDotNet writes it raw, a reader takes it for a byte order mark | intended (bug not reproduced: data loss) |
| O4 | `OmitEmptyCollections` keeps empty strings | `class C { string S = ""; }` with `ConfigureDefaultValuesHandling(OmitEmptyCollections)`: YamlDotNet drops `S` (a string is an `IEnumerable`) | intended (bug not reproduced) |
| O5 | `DateTimeOffset` is one ISO string | `Serialize(DateTimeOffset.UnixEpoch)` → YamlDotNet writes every property of the struct | intended (bug not reproduced) |
| O6 | `YamlStream.Save` keeps comments and layout of text that was loaded and not changed | load `# c\na:   1`, save: package returns the input; YamlDotNet re-emits `a: 1` | intended (kept comments) |
| O7 | After an edit `Save` re-emits from the model without comments; YamlDotNet adds `...` after each document, the package does not | `Save` of a built model: YamlDotNet ends with `...`, package does not | gap (no edit-sync into the lossless document; document end marker not written) |
| O8 | Anchors from `Save(w, assignAnchors: true)` are `o0`, `o1`..; YamlDotNet writes numeric names such as `&466756907` | any shared node | intended (bug not reproduced) |
| O9 | `Int128`, `UInt128` and `Half` are written as numbers | `Serialize((Int128)5)` → YamlDotNet writes `{}` (the value is lost), package `5`; the same for `UInt128` and `Half`. Reading them back: decimal numbers read the same in both; the package also reads `0x10` into `Int128` and `UInt128`, and `.inf`, `-.inf` and `.nan` into `Half` (the spellings it writes), where YamlDotNet throws; a number too large for a `Half` is an error here, where YamlDotNet gives infinity | intended (bug not reproduced: data loss) |

## Input (Deserialize / YamlStream.Load)

| # | Difference | Repro | Label |
|---|---|---|---|
| I1 | Spec-valid YAML YamlDotNet rejects is read | `{foo\n: bar}` (4MUZ/02); `foo: !!seq\n  - !!str a` with tagged collections (57H4, 6JWB, EHF6); `foo: 1\n<tab>\nbar: 2` (DK95/04); `k: {\n k\n :\n v\n }` (VJP3/01); `{ foo : !!str, !!str : bar }` (WZ62) | intended (spec) |
| I2 | YamlDotNet throws `ArgumentException` on some inputs, the package reads them | `- ? : x` and `- :` followed by an indented line (M2N8/00, UKK6/00) | intended (bug not reproduced) |
| I3 | Block scalar with kept trailing lines | the cases need their exact spaces: JEF9/01 is `- |+` followed by a line of three spaces (`- |+\n   \n`), spec `"\n"`; also JEF9/02 and L24T/01; YamlDotNet drops or adds a break | intended (spec) |
| I4 | Empty scalar tagged `!!str` | `- !!str` → spec `""`, YamlDotNet `null` (LE5A) | intended (spec) |
| I5 | Empty key | `{ ? foo :, : bar }` (FRK4): YamlDotNet reads key `""`, but refuses an empty key in every other shape (`: a`, `[ : x ]`); the package refuses all with "A mapping key cannot be null." | gap (YamlDotNet is inconsistent; the common case is matched) |
| I6 | `char` from a longer string | `Deserialize<char>("abc")` → YamlDotNet `'a'`, package throws | intended (bug not reproduced) |
| I7 | Unquoted decimals typed as `double` | `WithAttemptingUnquotedStringTypeDeserialization`: `123456789.123` → YamlDotNet `float` 123456790, package `double` | intended (bug not reproduced; integers match) |
| I8 | Empty member name | naming convention applied to `""` crashes YamlDotNet (`IndexOutOfRange`); package returns `""` | intended (bug not reproduced) |
| I9 | More collection targets | `Deserialize<ISet<string>>("[a]")`, `IReadOnlyDictionary<string,int>` → YamlDotNet throws, package reads | intended (more than the target) |
| I10 | Exception types | `SemanticErrorException` vs `SyntaxErrorException` cannot be told apart by the engine; syntax/encoding errors map to `SyntaxErrorException`, unresolved tags to `SemanticErrorException`, limits to `MaximumRecursionLevelReachedException`, undefined aliases to `AnchorNotFoundException` (by message text) | gap (the class of an error can differ) |
| I11 | Limits stay on | The deserializer enforces the alias budget and the depth limit. An alias into `object` or a collection gives back the same instance, and every use is charged to the alias budget as if it were expanded, so a billion-laughs input fails with a `YamlException` (an `AliasExpansionLimitExceededException`, a subclass of `MaximumRecursionLevelReachedException`, when the alias budget is the limit reached). For input you do not control, call `WithLimits(YamlLimits.Untrusted)` on the builder, or pass `YamlLimits.Untrusted` to `YamlStream.Load` or `new Parser(reader, limits)`. These three members are not in YamlDotNet, and a deserializer reading from such a parser keeps its limits. `WithMaximumRecursion` (default 130 read / 50 write) can lower the depth, never raise it above the library's depth limit. Representation-model walks (`ToString`, `Equals`, visitors) are bounded by the same node and text budgets. | intended (safety; compat never loosens limits) |
| I12 | Positions | node `Start`/`End` and exception marks use characters (UTF-16 units), so `Index` and `Column` count a character outside the Basic Multilingual Plane as two, as YamlDotNet does; collection `End` equals its `Start` as in YamlDotNet | matched |
| I12a | A leading byte order mark (U+FEFF) is not counted in positions | `Load(new StringReader("\uFEFFa: 1\nb: 2\n"))`: YamlDotNet counts the mark as one character, so from the end of the first node on, node and error `Index` values are one higher than for the same text without the mark. The package gives the positions of the text without the mark. YamlDotNet gives those positions too when the text comes through a `StreamReader`, which drops the mark, so its positions depend on how the text arrives; the package's are the same for a string, a `StringReader` or a `StreamReader` | intended (bug not reproduced: positions that depend on the input type) |
| I12b | Where a syntax error is placed | the error mark is normally at YamlDotNet's line and column but not always: `a: b: c` → YamlDotNet line 1, column 5, package column 6; `x: 1\nyy: 'q\n` (unclosed quote) → YamlDotNet at the opening quote (line 2, column 5), package at the end of the input (line 3, column 1). The units agree (see I12); only the point the error is reported at differs. The end of an error mark can also differ in width (`a: 1\nb: "😀" x\n` ends at column 10 in YamlDotNet, 9 in the package) | gap (the engine finds the error at another point) |

## Bugs not reproduced

| Area | Hazard | Package |
|---|---|---|
| Long keys | Keys longer than 1,024 characters are written as implicit keys, which readers reject | A key too long to be implicit is written after `? ` with the colon on its own line, for strings, for scalars of a loaded model, and in flow mappings. A key that is not a string (a long number, such as a `BigInteger` key) is written in its raw form after `? `, so it reads back as a number and not as a quoted string. |
| Anchor names | Generated anchor names can collide with names already in the model, so an alias can resolve to the wrong node on reading | Generated names skip every name the model already uses. |
| Nesting depth | Nesting bounded only by the recursion setting, so a huge setting can exhaust the stack | The effective depth is the smaller of the setting and the library's depth limit, and the stack is checked as well, on read and on write. |
| Model walks | Walks over a model built from aliases (`ToString`, `Equals`, visitors) have no bound; a billion-laughs model takes minutes or memory without limit | Every walk is bounded in depth, nodes and text; a walk past a bound ends in `MaximumRecursionLevelReachedException`. `GetHashCode` is not such a walk: it visits each distinct node once (see Key hashing). |
| Key hashing | Complex keys that agree on their first nodes are hard to tell apart by hash, so a mapping with thousands of such keys loads slowly (quadratic) | `GetHashCode` of a node covers the whole structure and depends on the structure alone, so equal nodes always hash alike however they share nodes and however large they are. A node shared by several parents is hashed once, so a mapping whose complex keys agree on a long prefix loads without the quadratic slowdown; while a document loads, its keys share a memo of hashed nodes that belongs to the load and is dropped when it ends. The walk keeps its own stack and takes time in proportion to the nodes in memory, never to the size of the unrolled tree, so it has no node or depth bound and never throws. A model that contains itself has no finite structure; its hash covers the node's kind, tag and size. |

## Surface not provided (gap)

`Core` `Scanner`, `MergingParser`, most `Parser` internals; `IObjectFactory`, node deserializers, type inspectors, event
emitters, object-graph visitors; `WithTypeDiscriminatingNodeDeserializer`, `WithEnforceNullability`/`WithEnforceRequiredMembers`;
static contexts (`StaticSerializerBuilder`); `WithYamlFormatter`; registration-location overloads; `YamlMappingNode.FromObject`;
comment events; `WithTypeResolver`. `EmitDefaults` is an error in YamlDotNet and absent here. `EnsureRoundtrip()` builds, but `Serialize` then throws `NotSupportedException`, since the adapter cannot do what it asks.
`ParserExtensions` has `Consume`/`Accept`/`TryConsume`-style members used by converters only; `Peek`/`Allow`/`Expect`/`TryFindMappingEntry` are missing.
`Deserialize(IParser)` accepts only the package's own `Parser`.

## Reproduced on purpose

`yes/on/y/true` (any case) read as booleans for typed targets; ints with `0x`, `0b`, leading-zero octal, `_`, base 60; `0o17` rejected; `.inf`/`.nan`;
null → default for value types; `Deserialize<T>("")` → default; last-wins duplicate keys (error with `WithDuplicateKeyChecking`); unknown property → `YamlException`;
read-only property counts as unknown; no `<<` merge; two documents with the string overloads → error; the six naming conventions (verified identifier by identifier).
