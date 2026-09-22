using System.Text.Json;
using Serilog.Core;
using Serilog.Events;

namespace BXLogger.Serilog;

/// <summary>
/// Captures <see cref="JsonDocument"/> and <see cref="JsonElement"/> as the structure they
/// hold rather than as the shape of their CLR types.
/// </summary>
/// <remarks>
/// <para>
/// Serilog's default destructurer reflects over public properties, and a
/// <see cref="JsonDocument"/> has exactly one: <c>RootElement</c>, whose own only property
/// is <c>ValueKind</c>. A DTO carrying a parsed request body therefore logs as
/// <c>Dto { Data: JsonDocument { RootElement: JsonElement { ValueKind: Object } } }</c> --
/// the payload, which is the one thing worth having when the event is an error, is gone by
/// the time the sink sees it.
/// </para>
/// <para>
/// This converts the JSON to Serilog's own value model instead, so it reaches BXLogger as
/// nested JSON and the server catalogues every path inside it as a filterable property.
/// The policy applies at any depth: registering it fixes a <c>JsonDocument</c> sitting
/// inside a destructured DTO, not just one logged directly.
/// </para>
/// <para>
/// <see cref="System.Text.Json.Nodes.JsonNode"/> is not handled. Nothing stops it being
/// added, but an untested conversion that silently mangles numbers would be worse than the
/// default reflection.
/// </para>
/// </remarks>
public sealed class JsonDestructuringPolicy : IDestructuringPolicy
{
    /// <summary>Matches the server's own path-extraction depth, so nothing is captured that could never be queried.</summary>
    public const int DefaultMaxDepth = 12;

    /// <summary>Members of one object, or elements of one array, captured before the rest are dropped.</summary>
    public const int DefaultMaxItems = 128;

    private readonly int _maxDepth;
    private readonly int _maxItems;

    /// <summary>Creates the policy.</summary>
    /// <param name="maxDepth">How deep to descend before falling back to raw text.</param>
    /// <param name="maxItems">How many members or elements to capture per container.</param>
    /// <remarks>
    /// Both limits are enforced here and not by Serilog. A policy that builds its values
    /// itself bypasses <c>MaximumDestructuringDepth</c> and the collection limits, so
    /// without these one pathological payload could turn into an event large enough to
    /// take a batch -- or the ingest endpoint -- down with it.
    /// </remarks>
    public JsonDestructuringPolicy(int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);

        _maxDepth = maxDepth;
        _maxItems = maxItems;
    }

    /// <inheritdoc />
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        switch (value)
        {
            case JsonDocument document:
                try
                {
                    result = Convert(document.RootElement, 0);
                }
                catch (ObjectDisposedException)
                {
                    // Capture is synchronous, so a live document is the normal case -- but a
                    // DTO that owns its JsonDocument can be disposed on a path that logs
                    // afterwards, and a logging call must not be what throws.
                    result = new ScalarValue("<disposed JsonDocument>");
                }

                return true;

            case JsonElement element:
                result = Convert(element, 0);
                return true;

            default:
                result = null!;
                return false;
        }
    }

    private LogEventPropertyValue Convert(JsonElement element, int depth) =>
        element.ValueKind switch
        {
            JsonValueKind.Object when depth < _maxDepth => new StructureValue(
                element.EnumerateObject()
                    // Serilog rejects a property with a blank name, and JSON permits one.
                    .Where(property => !string.IsNullOrWhiteSpace(property.Name))
                    .Take(_maxItems)
                    .Select(property => new LogEventProperty(property.Name, Convert(property.Value, depth + 1)))),

            JsonValueKind.Array when depth < _maxDepth => new SequenceValue(
                element.EnumerateArray().Take(_maxItems).Select(item => Convert(item, depth + 1))),

            JsonValueKind.String => new ScalarValue(element.GetString()),

            // decimal before double: a balance or a bet amount must not pass through binary
            // floating point on its way to a log that someone will reconcile against.
            JsonValueKind.Number => new ScalarValue(
                element.TryGetInt64(out var integer) ? integer
                : element.TryGetDecimal(out var dec) ? dec
                : element.GetDouble()),

            JsonValueKind.True => new ScalarValue(true),
            JsonValueKind.False => new ScalarValue(false),
            JsonValueKind.Null or JsonValueKind.Undefined => new ScalarValue(null),

            // Only reachable past the depth cap: keep the payload as text rather than drop it.
            _ => new ScalarValue(element.GetRawText()),
        };
}
