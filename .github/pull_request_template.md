## What this changes

<!-- One or two sentences: the problem and the fix. Link the issue if there is one. -->

## Checks

- [ ] `tools/gate` passes (it runs the same checks as CI: build, tests, format, public text, secret scan)
- [ ] A test fails without this change (or the change is documentation only)
- [ ] New or changed public members have XML doc comments, and new C# blocks in the documentation are doc samples
- [ ] `CHANGELOG.md` has a line under Unreleased for anything a user can see (breaking changes marked)
- [ ] No new dependency for the `OneYaml` package, and no `unsafe` code

## Performance

<!-- For a change to a hot path: BenchmarkDotNet results before and after, with allocations. Otherwise delete this section. -->
