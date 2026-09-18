using System.Net;
using System.Text;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.PeriodicBatching;

namespace BXLogger.Serilog;

/// <summary>
/// Ships batches of log events to a BXLogger server.
/// </summary>
/// <remarks>
/// Batching is handled by <c>Serilog.Sinks.PeriodicBatching</c>, so
/// <c>Log.Information(..)</c> only enqueues — it never touches the network. That is what
/// keeps logging off the application's critical path.
/// </remarks>
public sealed class BXLoggerSink : IBatchedLogEventSink, IDisposable
{
    private const string IngestPath = "/ingest";
    private const string ApiKeyHeader = "X-BXLogger-ApiKey";
    private const string ClientIdHeader = "X-BXLogger-ClientId";

    private readonly BXLoggerSinkOptions _options;
    private readonly ITextFormatter _formatter;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public BXLoggerSink(BXLoggerSinkOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _formatter = new BXLoggerClefFormatter(options.RenderMessage);

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // A pooled handler with a long connection lifetime, so batches ride a warm
            // TCP connection. See PooledConnectionLifetime for why this matters.
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = _options.PooledConnectionLifetime,
                PooledConnectionIdleTimeout = _options.PooledConnectionLifetime,
                MaxConnectionsPerServer = 4,
                EnableMultipleHttp2Connections = true,
            });
            _ownsHttpClient = true;
        }

        _httpClient.BaseAddress ??= new Uri(_options.Endpoint, UriKind.Absolute);
        _httpClient.Timeout = _options.HttpTimeout;
    }

    public async Task EmitBatchAsync(IEnumerable<LogEvent> batch)
    {
        if (batch is null)
        {
            return;
        }

        var payload = new StringBuilder(4096);

        using (var writer = new StringWriter(payload))
        {
            foreach (var logEvent in batch)
            {
                try
                {
                    _formatter.Format(logEvent, writer);
                }
                catch (Exception ex)
                {
                    // One unformattable event (usually a property whose ToString throws)
                    // must not cost the whole batch.
                    SelfLog.WriteLine("BXLogger: could not format an event: {0}", ex);
                }
            }
        }

        if (payload.Length == 0)
        {
            return;
        }

        using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var request = new HttpRequestMessage(HttpMethod.Post, IngestPath) { Content = content };

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.ApiKey);
        request.Headers.TryAddWithoutValidation(ClientIdHeader, _options.ClientId);

        using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Throwing tells PeriodicBatchingSink to retain the batch and retry with
        // backoff -- but only for failures that retrying could fix. A 400 means the
        // payload is wrong, and retrying it forever would wedge the queue.
        if (IsRetryable(response.StatusCode))
        {
            throw new HttpRequestException(
                $"BXLogger ingest failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        SelfLog.WriteLine(
            "BXLogger: dropping a batch the server rejected with {0}: {1}", response.StatusCode, body);
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
