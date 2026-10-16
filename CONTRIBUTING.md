# Contributing

## Set up

```bash
dotnet tool restore
dotnet husky install
```

The pre-commit hook formats staged `*.cs` files with CSharpier. CI fails when `dotnet csharpier check .` finds a diff.

## Build and test

```bash
dotnet build adaptive-rate-limiting-dotnet.slnx
dotnet test --solution adaptive-rate-limiting-dotnet.slnx
```

CI builds with warnings as errors. To get the same result locally, add `-p:CI=true`.

## Public API

Each public type and member must be in `PublicAPI.Unshipped.txt` of its project. The build fails with `RS0016` when one is missing. Use the code fix in the IDE to add it.

## Pull requests

- Use a [Conventional Commits](https://www.conventionalcommits.org/) title, such as `feat: add cubic controller`. Release Drafter labels the PR from the title.
- Add tests for each change in behavior. Use `FakeTimeProvider` for time, not `Task.Delay`.

## Release

1. Merge to `main`. Release Drafter updates the draft release.
2. Publish the draft release. The tag `vX.Y.Z` sets the package version through MinVer.
3. The `Build` workflow packs and pushes to nuget.org through Trusted Publishing.
