# Changelog

Versions of the `BXLogger.Serilog.Sink` package. The server is versioned separately —
this file covers the package alone.

The package follows [Semantic Versioning](https://semver.org/). Below 1.0.0 the minor
number is the breaking one, which is the usual reading of semver for a `0.x` library.

## Unreleased

### Fixed

- `WriteTo.BXLogger(..)` moved from the `BXLogger.Serilog` namespace to `Serilog`, which
  is where every Serilog sink puts its configuration extension. In 0.1.0 a consumer
  following the README got CS1061: the method was invisible without an undocumented
  `using BXLogger.Serilog;`. It went unnoticed because every call site in this repository
  already had that using. Source-compatible with 0.1.0 code, which needed `using Serilog;`
  for `LoggerConfiguration` regardless; binary-breaking for anything compiled against
  0.1.0. `BXLoggerSinkOptions` stays in `BXLogger.Serilog`.
- Package metadata carries a real project and repository URL. 0.1.0 published with the
  `PLACEHOLDER` value, so nuget.org showed no project link at all.

## 0.1.0

First release.

- `WriteTo.BXLogger(endpoint, clientId, apiKey, ..)` and a `BXLoggerSinkOptions`
  overload for configuration held elsewhere.
- Non-blocking batched delivery over `Serilog.Sinks.PeriodicBatching`, with a bounded
  queue so an unreachable server drops events rather than growing the heap.
- Newline-delimited CLEF carrying both `@mt` and `@m`, so events group by call site and
  free-text search still works.
- Selective retry: 5xx and 429 back off, 4xx drops the batch and reports through
  `Serilog.Debugging.SelfLog` rather than wedging the queue behind one bad event.
- Pooled connections held for 10 minutes, which keeps a batch off the ~40ms
  slow-start-plus-delayed-ACK path a cold connection pays.
- Targets net8.0, net9.0 and net10.0.
