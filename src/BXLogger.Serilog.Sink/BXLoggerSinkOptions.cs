namespace BXLogger.Serilog;

/// <summary>Configuration for <see cref="BXLoggerSink"/>.</summary>
public sealed class BXLoggerSinkOptions
{
    /// <summary>Base address of the BXLogger server, e.g. <c>http://localhost:29080</c>.</summary>
    public string Endpoint { get; set; } = "http://localhost:29080";

    /// <summary>Identifies the producing application. Every query is scoped by this.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Events per HTTP request.</summary>
    public int BatchSizeLimit { get; set; } = 500;

    /// <summary>How long a partial batch waits before being sent anyway.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum events held in memory when the server is unreachable. Past this, the
    /// oldest are dropped — an application must never run out of memory because its
    /// log server is down.
    /// </summary>
    public int QueueLimit { get; set; } = 100_000;

    /// <summary>
    /// Renders the message client-side and sends it as <c>@m</c>. Turning this off
    /// shifts the cost to the server, which renders from the template instead.
    /// </summary>
    public bool RenderMessage { get; set; } = true;

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a pooled connection is kept before being recycled.
    /// </summary>
    /// <remarks>
    /// Long on purpose. A cold TCP connection pays TCP slow-start, and a body larger
    /// than the initial congestion window then meets the OS delayed-ACK timer — which
    /// measured ~40ms per request against a warm-connection cost of ~1.6ms. Recycling
    /// connections aggressively would reintroduce that on a schedule.
    /// </remarks>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(Endpoint));
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new ArgumentException($"'{Endpoint}' is not an absolute URI.", nameof(Endpoint));
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException("A client id is required.", nameof(ClientId));
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(ApiKey));
        }

        if (BatchSizeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSizeLimit), BatchSizeLimit, "Must be positive.");
        }

        if (QueueLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueLimit), QueueLimit, "Must be positive.");
        }
    }
}
