using System.Net;
using System.Text.Json;
using BXLogger.Serilog;
using Serilog;
using Serilog.Events;
using Shouldly;

namespace BXLogger.Serilog.Sink.Tests;

/// <summary>
/// Covers <c>Destructure.Json()</c> end to end: a JSON payload logged through a real
/// Serilog pipeline and read back off the wire as the server would read it.
/// </summary>
/// <remarks>
/// Asserted through the sink rather than against the policy in isolation, because the
/// failure this exists to prevent is not a wrong <c>LogEventPropertyValue</c> -- it is a
/// payload that never reaches BXLogger.
/// </remarks>
public class JsonDestructuringTests
{
    /// <summary>Logs one event with the policy registered and returns the JSON it put on the wire.</summary>
    private static JsonElement Emit(Action<ILogger> log, Action<LoggerConfiguration>? configure = null)
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

        var configuration = new LoggerConfiguration().MinimumLevel.Is(LogEventLevel.Verbose);
        configure ??= c => c.Destructure.Json();
        configure(configuration);

        var logger = configuration.WriteTo.BXLogger(options, httpClient: httpClient).CreateLogger();

        log(logger);
        logger.Dispose(); // Flushes the batching sink.

        var line = handler.Requests
            .SelectMany(body => body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ShouldHaveSingleItem();

        return JsonDocument.Parse(line).RootElement;
    }

    [Fact]
    public void Without_the_policy_a_document_captures_as_the_shape_of_its_clr_type()
    {
        // The bug this policy exists for. Kept as a test so the default behaviour is on
        // record: if a later Serilog makes JsonDocument work out of the box, this fails
        // and the policy can go.
        using var document = JsonDocument.Parse("""{"event":"BET","amount":250}""");

        var captured = Emit(l => l.Error("Request rejected {@Dto}", document), configure: _ => { });

        captured.GetProperty("Dto").TryGetProperty("event", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_document_is_captured_as_the_json_it_holds()
    {
        using var document = JsonDocument.Parse("""{"event":"BET","amount":250,"live":true}""");

        var dto = Emit(l => l.Error("Request rejected {@Dto}", document)).GetProperty("Dto");

        dto.ValueKind.ShouldBe(JsonValueKind.Object);
        dto.GetProperty("event").GetString().ShouldBe("BET");
        dto.GetProperty("amount").GetInt32().ShouldBe(250);
        dto.GetProperty("live").ValueKind.ShouldBe(JsonValueKind.True);
    }

    [Fact]
    public void A_document_nested_in_a_destructured_object_is_captured_too()
    {
        // The real call site: the JsonDocument is a property of a DTO, so the policy is
        // reached through Serilog's value factory rather than called on the top value.
        using var document = JsonDocument.Parse("""{"amount":250}""");

        var dto = Emit(l => l.Error("Request rejected {@Dto}", new { Event = "BET", Data = document }))
            .GetProperty("Dto");

        dto.GetProperty("Event").GetString().ShouldBe("BET");
        dto.GetProperty("Data").GetProperty("amount").GetInt32().ShouldBe(250);
    }

    [Fact]
    public void Nesting_and_arrays_survive()
    {
        using var document = JsonDocument.Parse("""{"bet":{"lines":[{"sku":"a"},{"sku":"b"}]}}""");

        var lines = Emit(l => l.Error("{@Dto}", document))
            .GetProperty("Dto")
            .GetProperty("bet")
            .GetProperty("lines");

        lines.ValueKind.ShouldBe(JsonValueKind.Array);
        lines.EnumerateArray().Select(e => e.GetProperty("sku").GetString()).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Json_types_survive_as_json_types()
    {
        // JSONB containment is type-aware: a number arriving as a string makes a filter on
        // amount=250 silently stop matching.
        using var document = JsonDocument.Parse("""{"n":250,"d":1.5,"s":"250","t":true,"f":false,"z":null}""");

        var dto = Emit(l => l.Error("{@Dto}", document)).GetProperty("Dto");

        dto.GetProperty("n").ValueKind.ShouldBe(JsonValueKind.Number);
        dto.GetProperty("d").GetDouble().ShouldBe(1.5);
        dto.GetProperty("s").ValueKind.ShouldBe(JsonValueKind.String);
        dto.GetProperty("t").ValueKind.ShouldBe(JsonValueKind.True);
        dto.GetProperty("f").ValueKind.ShouldBe(JsonValueKind.False);
        dto.GetProperty("z").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void A_large_number_keeps_its_precision()
    {
        // Through double, 9007199254740993 comes back as ...992.
        using var document = JsonDocument.Parse("""{"id":9007199254740993,"amount":250.10}""");

        var dto = Emit(l => l.Error("{@Dto}", document)).GetProperty("Dto");

        dto.GetProperty("id").GetInt64().ShouldBe(9007199254740993);
        dto.GetProperty("amount").GetDecimal().ShouldBe(250.10m);
    }

    [Fact]
    public void An_element_can_be_logged_on_its_own()
    {
        using var document = JsonDocument.Parse("""{"amount":250}""");

        var dto = Emit(l => l.Error("{@Dto}", document.RootElement)).GetProperty("Dto");

        dto.GetProperty("amount").GetInt32().ShouldBe(250);
    }

    [Fact]
    public void Past_the_depth_limit_the_rest_is_kept_as_text()
    {
        // Dropping it would lose the payload the policy exists to keep; descending forever
        // would let one pathological event size the batch.
        using var document = JsonDocument.Parse("""{"a":{"b":{"c":{"deep":1}}}}""");

        var dto = Emit(l => l.Error("{@Dto}", document), c => c.Destructure.Json(maxDepth: 2))
            .GetProperty("Dto");

        var b = dto.GetProperty("a").GetProperty("b");

        b.ValueKind.ShouldBe(JsonValueKind.String);
        b.GetString().ShouldNotBeNull().ShouldContain("deep");
    }

    [Fact]
    public void An_oversized_container_is_truncated()
    {
        var items = string.Join(',', Enumerable.Range(0, 50));
        using var document = JsonDocument.Parse($$"""{"items":[{{items}}]}""");

        var dto = Emit(l => l.Error("{@Dto}", document), c => c.Destructure.Json(maxItems: 10))
            .GetProperty("Dto");

        dto.GetProperty("items").GetArrayLength().ShouldBe(10);
    }

    [Fact]
    public void A_disposed_document_does_not_throw_out_of_the_logging_call()
    {
        // A DTO that owns its document can be disposed on a path that logs afterwards. The
        // event loses its payload; it must not lose the exception it was reporting.
        var document = JsonDocument.Parse("""{"amount":250}""");
        document.Dispose();

        var captured = Emit(l => l.Error("Request rejected {@Dto}", document));

        captured.GetProperty("Dto").ValueKind.ShouldBe(JsonValueKind.String);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"accepted":1}"""),
            };
        }
    }
}
