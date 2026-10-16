# Stability

What a release of oneyaml-csharp promises, and which .NET versions it
supports. This is the only place this policy is stated; other pages
link here.

## Versions

From 1.0.0 the packages follow [Semantic Versioning](https://semver.org/).
Within a major version:

- No change breaks code that compiles against the public API of
  `OneYaml`, `OneYaml.Compat.YamlDotNet` or `OneYaml.Compat.SharpYaml`.
  That covers `YamlException` and its `Category`; the text of messages
  is written for people and may change in any release.
- The text that `YamlSerializer.Serialize` writes with default options
  does not change, and neither do the bytes an edit leaves untouched,
  except to fix output that is incorrect. Such a fix is listed in the
  changelog.

A change that can break your code comes only in a new major version,
and the [changelog](../CHANGELOG.md) marks it **Breaking**, with what to
write instead.

## Supported .NET versions

The packages target `net8.0` and `net10.0`. CI builds and tests both on
Linux, macOS and Windows. Dropping a target framework takes a new major
version.

Security fixes go into the latest release; see [Security](../SECURITY.md).
