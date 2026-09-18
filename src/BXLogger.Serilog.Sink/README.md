# BXLogger.Serilog.Sink

Serilog sink that ships structured log events to a BXLogger server — a structured log
server in the spirit of Seq, which indexes every structured property on write, so
there is nothing to declare or register before you can query on it.

Targets net8.0, net9.0 and net10.0.

## Install

```bash
dotnet add package BXLogger.Serilog.Sink
```

## Use

```csharp
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

## ASP.NET Core

```csharp
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.BXLogger(
        endpoint: builder.Configuration["BXLogger:Sink:Endpoint"]!,
        clientId: builder.Configuration["BXLogger:Sink:ClientId"]!,
        apiKey: builder.Configuration["BXLogger:Sink:ApiKey"]!));
```

## Options

| Option | Default | Notes |
|---|---|---|
| `batchSizeLimit` | 500 | Events per HTTP request. |
| `period` | 2s | How long a partial batch waits. |
| `queueLimit` | 100,000 | Bounded, so an unreachable server cannot exhaust memory. |
| `renderMessage` | `true` | Renders `@m` client-side. Set `false` to shift the cost to the server. |
| `httpClient` | `null` | Supply one to route through `IHttpClientFactory`. |

## Behaviour worth knowing

**Logging never blocks.** `Log.Information(..)` enqueues; a background batcher does the
HTTP. Nothing in your request path waits on the log server.

**The queue is bounded.** If BXLogger is unreachable, events accumulate to `queueLimit`
and then the oldest are dropped. An application should not die because its log server did.

**Retries are selective.** 5xx and 429 are retried with backoff. A 4xx means the payload
itself is wrong, so the batch is dropped and reported via `Serilog.Debugging.SelfLog` —
retrying it forever would wedge the queue behind one bad event.

**Connections are kept warm.** A cold TCP connection pays slow-start, and a batch larger
than the initial congestion window then meets the OS delayed-ACK timer, which costs about
40ms per request versus ~1.6ms on a warm connection. The sink holds pooled connections for
10 minutes by default. If you pass your own `HttpClient`, give it a handler configured the
same way.

**Diagnosing silence.** Turn on Serilog's self-log:

```csharp
Serilog.Debugging.SelfLog.Enable(Console.Error);
```

## Wire format

Newline-delimited [CLEF](https://clef-json.org/), posted to `/ingest`. Both `@mt` (the
template, which groups events by call site) and `@m` (the rendered text, which free-text
search searches) are sent — Serilog's own compact formatters each emit only one of the two.

## Versioning

[Semantic Versioning](https://semver.org/); below 1.0.0 the minor number is the breaking
one. `CHANGELOG.md`, beside this file in the repository, records what changed in each release.
