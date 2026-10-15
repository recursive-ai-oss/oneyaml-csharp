# OneYaml.Compat.YamlDotNet: differences from YamlDotNet 18.1.0

The package reproduces the documented behaviour and API of YamlDotNet 18.1.0 for the serializer builders and the
representation model, never its defects. Every difference below is checked against the real library by a differential test
in `tests/OneYaml.Compat.Tests`. Switching is a package swap and a `using` change: `using YamlDotNet.Serialization;` becomes
`using OneYaml.Compat.YamlDotNet.Serialization;` (and likewise for `RepresentationModel` and `Core`).

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
| I11 | Limits stay on | The deserializer enforces the alias budget and depth (`WithMaximumRecursion`, default 130 read / 50 write, never above the library's depth limit); a billion-laughs input fails with a `YamlException`. Representation-model walks (`ToString`, `GetHashCode`, `Equals`, visitors) are bounded by the same node and text budgets. | intended (safety; compat never loosens limits) |
| I12 | Positions | node `Start`/`End` and exception marks use characters (UTF-16 units); collection `End` equals its `Start` as in YamlDotNet | matched |

## Bugs not reproduced

| # | Hazard | Package |
|---|---|---|
| B1 | Keys longer than 1,024 characters are written as implicit keys, which readers reject | A key too long to be implicit is written after `? ` with the colon on its own line, for strings, for scalars of a loaded model, and in flow mappings. A key that is not a string (a long number, such as a `BigInteger` key) is written in its raw form after `? `, so it reads back as a number and not as a quoted string. |
| B2 | Generated anchor names can collide with names already in the model, so an alias can resolve to the wrong node on reading | Generated names skip every name the model already uses. |
| B3 | Nesting bounded only by the recursion setting, so a huge setting can exhaust the stack | The effective depth is the smaller of the setting and the library's depth limit, and the stack is checked as well, on read and on write. |
| B4 | Walks over a model built from aliases (`ToString`, `GetHashCode`, `Equals`, visitors) have no bound; a billion-laughs model takes minutes or memory without limit | Every walk is bounded in depth, nodes and text; a walk past a bound ends in `MaximumRecursionLevelReachedException`. |
| B5 | Complex keys that agree on their first nodes are hard to tell apart by hash, so a mapping with thousands of such keys loads slowly (quadratic) | `GetHashCode` of a node covers the whole structure, and a node shared by several parents is hashed once, so a mapping whose complex keys agree on a long prefix loads without the quadratic slowdown. While a document loads, all its keys share one memo of hashed nodes. A key past the node budget ends in `MaximumRecursionLevelReachedException` when loading; outside a load `GetHashCode` never throws and falls back to a hash of the node's kind, tag and size. Equal nodes always hash alike. |

## Surface not provided (gap)

`Core` `Scanner`, `MergingParser`, most `Parser` internals; `IObjectFactory`, node deserializers, type inspectors, event
emitters, object-graph visitors; `WithTypeDiscriminatingNodeDeserializer`, `WithEnforceNullability`/`WithEnforceRequiredMembers`;
static contexts (`StaticSerializerBuilder`); `WithYamlFormatter`; registration-location overloads; `YamlMappingNode.FromObject`;
comment events; `WithTypeResolver`. `EmitDefaults` is an error in YamlDotNet and absent here. `EnsureRoundtrip` is accepted and has no effect.
`ParserExtensions` has `Consume`/`Accept`/`TryConsume`-style members used by converters only; `Peek`/`Allow`/`Expect`/`TryFindMappingEntry` are missing.
`Deserialize(IParser)` accepts only the package's own `Parser`.

## Reproduced on purpose

`yes/on/y/true` (any case) read as booleans for typed targets; ints with `0x`, `0b`, leading-zero octal, `_`, base 60; `0o17` rejected; `.inf`/`.nan`;
null → default for value types; `Deserialize<T>("")` → default; last-wins duplicate keys (error with `WithDuplicateKeyChecking`); unknown property → `YamlException`;
read-only property counts as unknown; no `<<` merge; two documents with the string overloads → error; the six naming conventions (verified identifier by identifier).
