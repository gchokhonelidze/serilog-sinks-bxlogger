using System.Globalization;
using System.Net;
using System.Text.Json;
using BXLogger.Serilog;
using Serilog;
using Serilog.Events;
using Shouldly;

namespace BXLogger.Serilog.Sink.Tests;

/// <summary>
/// Drives a real Serilog pipeline through the sink and asserts on the CLEF it puts on
/// the wire.
/// </summary>
/// <remarks>
/// <para>
/// These assert the sink's half of the contract: that what leaves the process is
/// well-formed CLEF carrying the fields BXLogger expects. The server's half -- that its
/// parser accepts this -- is asserted in the server's own repository against the
/// published package, so the two can be released independently without either having to
/// build the other.
/// </para>
/// <para>
/// Asserting on the JSON rather than on a parser's output is deliberate. A change that
/// moved the sink and a parser the same way would pass a round-trip test and still break
/// every consumer already on a released version.
/// </para>
/// </remarks>
public class SinkWireFormatTests
{
    private static (List<CapturedRequest> Requests, ILogger Logger, IDisposable Scope) CreateLogger(
        Action<BXLoggerSinkOptions>? configure = null,
        Action<LoggerConfiguration>? enrich = null,
        LogEventLevel minimumLevel = LogEventLevel.Verbose)
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:29080") };

        var options = new BXLoggerSinkOptions
        {
            Endpoint = "http://localhost:29080",
            ClientId = "test-client",
            ApiKey = "test-key",
            BatchSizeLimit = 1000,
            Period = TimeSpan.FromMilliseconds(50),
        };

        configure?.Invoke(options);

        var configuration = new LoggerConfiguration().MinimumLevel.Is(minimumLevel);
        enrich?.Invoke(configuration);

        var logger = configuration
            .WriteTo.BXLogger(options, httpClient: httpClient)
            .CreateLogger();

        return (handler.Requests, logger, logger);
    }

    /// <summary>Logs, flushes, and returns one parsed JSON object per event sent.</summary>
    private static List<JsonElement> Emit(
        Action<ILogger> log,
        Action<BXLoggerSinkOptions>? configure = null,
        Action<LoggerConfiguration>? enrich = null)
    {
        var (requests, logger, scope) = CreateLogger(configure, enrich);

        log(logger);
        scope.Dispose(); // Flushes the batching sink.

        return requests.SelectMany(Lines).ToList();
    }

    /// <summary>
    /// Splits a batch into lines and parses each separately, which is what the server
    /// does: newline-delimited JSON, every line standing on its own.
    /// </summary>
    private static IEnumerable<JsonElement> Lines(CapturedRequest request) =>
        request.Body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    [Fact]
    public void An_event_is_one_json_object_on_one_line()
    {
        var (requests, logger, scope) = CreateLogger();

        logger.Information("hello");
        scope.Dispose();

        var body = requests.ShouldHaveSingleItem().Body;

        body.ShouldEndWith("\n");
        body.TrimEnd('\n').ShouldNotContain("\n");
    }

    [Fact]
    public void The_template_is_sent_verbatim()
    {
        // The template is what groups events by call site, so it travels unrendered.
        var events = Emit(l => l.Information("User {UserId} logged in from {Ip}", 42, "10.0.0.1"));

        Text(events.ShouldHaveSingleItem(), "@mt").ShouldBe("User {UserId} logged in from {Ip}");
    }

    [Fact]
    public void The_rendered_message_is_sent_alongside_the_template()
    {
        // Serilog's own compact formatters emit one or the other. BXLogger needs both:
        // @m is what free-text search searches, @mt is what groups by call site.
        var events = Emit(l => l.Information("User {UserId} logged in from {Ip}", 42, "10.0.0.1"));

        Text(events[0], "@m").ShouldBe("User 42 logged in from \"10.0.0.1\"");
        Text(events[0], "@mt").ShouldBe("User {UserId} logged in from {Ip}");
    }

    [Fact]
    public void The_rendered_message_is_omitted_when_it_would_repeat_the_template()
    {
        // A line with no properties renders to its own template, so sending both would
        // put the same string on the wire twice for every such event.
        var events = Emit(l => l.Information("a message with no properties"));

        events[0].TryGetProperty("@m", out _).ShouldBeFalse();
        Text(events[0], "@mt").ShouldBe("a message with no properties");
    }

    [Fact]
    public void Rendering_can_be_shifted_to_the_server()
    {
        var events = Emit(
            l => l.Information("User {UserId} did {Action}", 42, "login"),
            o => o.RenderMessage = false);

        events[0].TryGetProperty("@m", out _).ShouldBeFalse();
        Text(events[0], "@mt").ShouldBe("User {UserId} did {Action}");
    }

    [Fact]
    public void Information_carries_no_level_because_clef_treats_it_as_the_default()
    {
        var events = Emit(l => l.Information("hello"));

        events[0].TryGetProperty("@l", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "Verbose")]
    [InlineData(LogEventLevel.Debug, "Debug")]
    [InlineData(LogEventLevel.Warning, "Warning")]
    [InlineData(LogEventLevel.Error, "Error")]
    [InlineData(LogEventLevel.Fatal, "Fatal")]
    public void Every_other_level_is_sent_by_name(LogEventLevel level, string expected)
    {
        var events = Emit(l => l.Write(level, "message"));

        Text(events.ShouldHaveSingleItem(), "@l").ShouldBe(expected);
    }

    [Fact]
    public void The_timestamp_is_utc_and_parses_as_iso_8601()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var events = Emit(l => l.Information("hello"));

        var timestamp = DateTimeOffset.Parse(
            Text(events[0], "@t")!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        timestamp.ShouldBeGreaterThan(before);
        timestamp.ShouldBeLessThan(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Property_types_survive_as_json_types()
    {
        // JSONB containment is type-aware, so an int arriving as a string would make
        // a property filter on UserId=42 silently stop matching.
        var events = Emit(l => l.Information("{Int} {Double} {Bool} {Str}", 42, 1.5, true, "text"));

        var e = events[0];

        e.GetProperty("Int").ValueKind.ShouldBe(JsonValueKind.Number);
        e.GetProperty("Int").GetInt32().ShouldBe(42);
        e.GetProperty("Double").GetDouble().ShouldBe(1.5);
        e.GetProperty("Bool").ValueKind.ShouldBe(JsonValueKind.True);
        e.GetProperty("Str").GetString().ShouldBe("text");
    }

    [Fact]
    public void Exceptions_are_sent_under_the_reserved_exception_key()
    {
        var events = Emit(l =>
            l.Error(new InvalidOperationException("boom"), "Operation {Name} failed", "checkout"));

        Text(events[0], "@x").ShouldNotBeNull().ShouldContain("boom");
        Text(events[0], "@l").ShouldBe("Error");
    }

    [Fact]
    public void Destructured_objects_are_sent_as_nested_json()
    {
        var events = Emit(l => l.Information("Order {@Order}", new { Id = 7, Total = 99.5 }));

        var order = events[0].GetProperty("Order");

        order.ValueKind.ShouldBe(JsonValueKind.Object);
        order.GetProperty("Id").GetInt32().ShouldBe(7);
    }

    [Fact]
    public void Enriched_properties_are_sent()
    {
        var events = Emit(
            l => l.Information("hello"),
            enrich: c => c.Enrich.WithProperty("Environment", "test"));

        events[0].GetProperty("Environment").GetString().ShouldBe("test");
    }

    [Fact]
    public void A_property_named_like_a_reserved_key_is_escaped_by_doubling_the_sigil()
    {
        // Without the escape, a property genuinely called "@x" would be read by the
        // server as the exception.
        var events = Emit(l => l.ForContext("@x", "mine").Information("hello"));

        events[0].TryGetProperty("@x", out _).ShouldBeFalse();
        events[0].GetProperty("@@x").GetString().ShouldBe("mine");
    }

    [Fact]
    public void Unicode_survives_the_round_trip()
    {
        var events = Emit(l => l.Information("Gruesse {Name}", "Unicode check 日本語"));

        Text(events[0], "@m").ShouldNotBeNull().ShouldContain("日本語");
    }

    [Fact]
    public void Every_line_of_a_batch_is_valid_json_on_its_own()
    {
        // The server parses line by line, so one malformed line must not be able to take
        // the rest of the batch with it.
        var events = Emit(l =>
        {
            for (var i = 0; i < 25; i++)
            {
                l.Information("Event {N}", i);
            }
        });

        events.Count.ShouldBe(25);
        events.Select(e => e.GetProperty("N").GetInt32()).ShouldBe(Enumerable.Range(0, 25));
    }

    [Fact]
    public void Credentials_are_sent_as_headers()
    {
        var (requests, logger, scope) = CreateLogger();

        logger.Information("hello");
        scope.Dispose();

        var request = requests.ShouldHaveSingleItem();

        request.ApiKey.ShouldBe("test-key");
        request.ClientId.ShouldBe("test-client");
    }

    [Fact]
    public void Events_below_the_minimum_level_never_reach_the_network()
    {
        var (requests, logger, scope) = CreateLogger(minimumLevel: LogEventLevel.Warning);

        logger.Information("filtered out");
        logger.Debug("also filtered");
        scope.Dispose();

        requests.ShouldBeEmpty();
    }

    private sealed record CapturedRequest(string Body, string? ApiKey, string? ClientId);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                body,
                request.Headers.TryGetValues("X-BXLogger-ApiKey", out var k) ? k.FirstOrDefault() : null,
                request.Headers.TryGetValues("X-BXLogger-ClientId", out var c) ? c.FirstOrDefault() : null));

            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"accepted":1}"""),
            };
        }
    }
}
