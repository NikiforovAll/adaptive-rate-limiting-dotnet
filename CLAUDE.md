# CLAUDE.md

## Project

`Nall.RateLimiting.Adaptive`: adaptive (AIMD) client-side rate limiting for .NET `HttpClient`. The limiter shape comes from `System.Threading.RateLimiting`; this library owns only the controller that changes the rate. `Nall.RateLimiting.Adaptive.MicrosoftGraph` adds Graph and SharePoint resource-unit costs. Targets `net8.0;net10.0`.

## Commands

```bash
dotnet tool restore
dotnet build adaptive-rate-limiting-dotnet.slnx -p:CI=true
dotnet test --solution adaptive-rate-limiting-dotnet.slnx
dotnet csharpier check .
dotnet pack adaptive-rate-limiting-dotnet.slnx -c Release -o ./Artefacts
```

Tests run on TUnit over Microsoft.Testing.Platform (`global.json` sets the runner). Filter with `--treenode-filter '/*/*/<Class>/<Method>'`, not `--filter`.

## Rules

- No reflection in the core: the package is `IsAotCompatible`.
- Each public API change goes in `PublicAPI.Unshipped.txt`.
- Library code uses `ConfigureAwait(false)` (CA2007 is a warning, an error in CI).
- Time comes from `TimeProvider`. Tests use `FakeTimeProvider`, never `Task.Delay`.
- Metrics use `IMeterFactory` with fixed instrument names `ratelimit.adaptive.*`. No metrics switches in options.
