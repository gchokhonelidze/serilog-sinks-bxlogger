using BXLogger.Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Sinks.PeriodicBatching;

// Serilog's own convention, which every first-party sink follows: the WriteTo extension
// lives in the `Serilog` namespace, so `using Serilog;` is all a consumer needs. Declared
// in BXLogger.Serilog it compiled fine in this repository -- every call site here already
// had that using -- and left consumers with a method they could not find.
namespace Serilog;

/// <summary>Adds <c>WriteTo.BXLogger(..)</c> to Serilog's fluent configuration.</summary>
public static class BXLoggerSinkExtensions
{
    /// <summary>Writes log events to a BXLogger server.</summary>
    /// <param name="sinkConfiguration">The Serilog sink configuration being extended.</param>
    /// <param name="endpoint">Base address of the BXLogger server.</param>
    /// <param name="clientId">Identifies the producing application.</param>
    /// <param name="apiKey">The key issued for <paramref name="clientId"/>.</param>
    /// <param name="restrictedToMinimumLevel">Events below this never leave the process.</param>
    /// <param name="batchSizeLimit">Events per HTTP request.</param>
    /// <param name="period">How long a partial batch waits before being sent anyway.</param>
    /// <param name="queueLimit">
    /// Maximum events buffered while the server is unreachable, after which the oldest
    /// are dropped. Bounded so a downed log server cannot exhaust the application's memory.
    /// </param>
    /// <param name="renderMessage">
    /// Renders the message client-side. When false, the server renders from the template.
    /// </param>
    /// <param name="httpClient">
    /// Supply one to route through <c>IHttpClientFactory</c> or to test the sink without
    /// a network. When null, the sink owns a pooled client tuned for keep-alive.
    /// </param>
    /// <returns>The logger configuration, for chaining.</returns>
    public static LoggerConfiguration BXLogger(
        this LoggerSinkConfiguration sinkConfiguration,
        string endpoint,
        string clientId,
        string apiKey,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        int batchSizeLimit = 500,
        TimeSpan? period = null,
        int queueLimit = 100_000,
        bool renderMessage = true,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);

        var options = new BXLoggerSinkOptions
        {
            Endpoint = endpoint,
            ClientId = clientId,
            ApiKey = apiKey,
            BatchSizeLimit = batchSizeLimit,
            Period = period ?? TimeSpan.FromSeconds(2),
            QueueLimit = queueLimit,
            RenderMessage = renderMessage,
        };

        return sinkConfiguration.BXLogger(options, restrictedToMinimumLevel, httpClient);
    }

    /// <summary>Writes log events to a BXLogger server using a pre-built options object.</summary>
    /// <param name="sinkConfiguration">The Serilog sink configuration being extended.</param>
    /// <param name="options">Sink configuration.</param>
    /// <param name="restrictedToMinimumLevel">Events below this never leave the process.</param>
    /// <param name="httpClient">
    /// Supply one to route through <c>IHttpClientFactory</c> or to test the sink without
    /// a network. When null, the sink owns a pooled client tuned for keep-alive.
    /// </param>
    /// <returns>The logger configuration, for chaining.</returns>
    public static LoggerConfiguration BXLogger(
        this LoggerSinkConfiguration sinkConfiguration,
        BXLoggerSinkOptions options,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        var sink = new BXLoggerSink(options, httpClient);

        var batching = new PeriodicBatchingSink(sink, new PeriodicBatchingSinkOptions
        {
            BatchSizeLimit = options.BatchSizeLimit,
            Period = options.Period,
            // Bounded: if the server is unreachable, the application drops logs rather
            // than growing its heap until it dies.
            QueueLimit = options.QueueLimit,
            EagerlyEmitFirstEvent = true,
        });

        return sinkConfiguration.Sink(batching, restrictedToMinimumLevel);
    }
}
