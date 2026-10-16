# Nall.RateLimiting.Adaptive

Adaptive client-side rate limiting for .NET `HttpClient`, with pluggable rate controllers (AIMD, CUBIC), `Retry-After` support and a shared budget across processes. Includes Microsoft Graph and SharePoint support.

Work in progress. Not published yet.

## Build

```bash
dotnet tool restore
dotnet husky install
dotnet build adaptive-rate-limiting-dotnet.slnx
dotnet test --solution adaptive-rate-limiting-dotnet.slnx
```
