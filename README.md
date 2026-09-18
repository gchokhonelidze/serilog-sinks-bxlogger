# serilog-sinks-bxlogger

[![NuGet](https://img.shields.io/nuget/v/BXLogger.Serilog.Sink.svg)](https://www.nuget.org/packages/BXLogger.Serilog.Sink/)

A Serilog sink that ships structured log events to a [BXLogger](https://www.nuget.org/packages/BXLogger.Serilog.Sink/)
server over HTTP using CLEF.

```bash
dotnet add package BXLogger.Serilog.Sink
```

```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.BXLogger(
        endpoint: "http://localhost:29080",
        clientId: "checkout-api",
        apiKey: "your-key")
    .CreateLogger();

Log.Information("User {UserId} checked out {ItemCount} items", 42, 3);
```

`UserId` and `ItemCount` are queryable immediately — BXLogger indexes structured
properties on write, so there is nothing to declare or register.

Full usage, options and behaviour: **[src/BXLogger.Serilog.Sink/README.md](src/BXLogger.Serilog.Sink/README.md)**,
which is also what nuget.org shows on the package page.

Targets net8.0, net9.0 and net10.0.

## Build and test

```powershell
./test.ps1      # build and run the test suite
./pack.ps1      # test, pack into artifacts/, verify the package
```

`dotnet test` is not usable here. .NET 10 removed the VSTest bridge, and the
Microsoft.Testing.Platform host in SDK 10.0.1xx will not launch xunit.v3 4.0.1 test
applications, reporting "Zero tests ran" without ever starting the process. The test
project is a self-hosting executable and the scripts run it directly.

## What the tests cover, and what they deliberately do not

The tests drive a real Serilog pipeline through the sink and assert on the CLEF that
reaches the wire: `@t`, `@mt`, `@m`, `@l`, `@x`, JSON-typed properties, the `@@` escape
for property names that would collide with a reserved key, and one self-contained JSON
object per line.

They assert the sink's half of the contract only. That the *server* accepts this is
asserted in the BXLogger server's own repository, against the published package — so the
two release independently, and neither has to build the other to be tested.

That split is deliberate. A change that moved the sink and the server's parser the same
way would pass a round-trip test and still break every consumer already on a released
version.

## Releasing

`./pack.ps1` builds into `artifacts/` and inspects the result before it will let anything
be pushed. `./pack.ps1 -Push` publishes to nuget.org using `$env:NUGET_API_KEY`, after
asking for the version back.

Version and release notes live in
[src/BXLogger.Serilog.Sink/BXLogger.Serilog.Sink.csproj](src/BXLogger.Serilog.Sink/BXLogger.Serilog.Sink.csproj)
and [CHANGELOG.md](src/BXLogger.Serilog.Sink/CHANGELOG.md).

A published version is permanent: it can be unlisted, never replaced.

## Licence

MIT. See [LICENSE](LICENSE).
