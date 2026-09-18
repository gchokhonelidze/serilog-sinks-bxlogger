using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace BXLogger.Serilog;

/// <summary>
/// Writes a Serilog event as a single line of CLEF.
/// </summary>
/// <remarks>
/// Serilog ships two compact formatters and neither is quite right here:
/// <c>CompactJsonFormatter</c> emits <c>@mt</c> (the template) but no rendered text, and
/// <c>RenderedCompactJsonFormatter</c> emits <c>@m</c> but drops the template. BXLogger
/// wants both — the template is what groups events by call site, and the rendered text
/// is what free-text search actually searches — so this writes them together.
/// </remarks>
public sealed class BXLoggerClefFormatter : ITextFormatter
{
    /// <summary>
    /// Rendering costs a string build per event, so it is skippable for callers who
    /// care more about the logging application's throughput than about server-side
    /// search. The server renders the template itself when <c>@m</c> is absent.
    /// </summary>
    private readonly bool _renderMessage;

    public BXLoggerClefFormatter(bool renderMessage = true) => _renderMessage = renderMessage;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write("{\"@t\":\"");
        output.Write(logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        output.Write('"');

        output.Write(",\"@mt\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);

        if (_renderMessage)
        {
            var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);

            // Only worth the bytes when it actually differs from the template, which it
            // does not for the many log lines that have no properties at all.
            if (!string.Equals(rendered, logEvent.MessageTemplate.Text, StringComparison.Ordinal))
            {
                output.Write(",\"@m\":");
                JsonValueFormatter.WriteQuotedJsonString(rendered, output);
            }
        }

        // CLEF omits @l for Information, which is both the convention and the common case.
        if (logEvent.Level != LogEventLevel.Information)
        {
            output.Write(",\"@l\":\"");
            output.Write(logEvent.Level);
            output.Write('"');
        }

        if (logEvent.Exception is not null)
        {
            output.Write(",\"@x\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
        }

        if (logEvent.TraceId is { } traceId)
        {
            output.Write(",\"@tr\":\"");
            output.Write(traceId.ToHexString());
            output.Write('"');
        }

        if (logEvent.SpanId is { } spanId)
        {
            output.Write(",\"@sp\":\"");
            output.Write(spanId.ToHexString());
            output.Write('"');
        }

        foreach (var property in logEvent.Properties)
        {
            var name = property.Key;

            // A property genuinely named "@x" would collide with a reserved key, so CLEF
            // escapes it by doubling the sigil. The server reverses this.
            if (name.Length > 0 && name[0] == '@')
            {
                name = "@" + name;
            }

            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(name, output);
            output.Write(':');
            ValueFormatter.Format(property.Value, output);
        }

        output.Write('}');
        output.Write('\n');
    }

    private static readonly JsonValueFormatter ValueFormatter = new(typeTagName: "$type");
}
