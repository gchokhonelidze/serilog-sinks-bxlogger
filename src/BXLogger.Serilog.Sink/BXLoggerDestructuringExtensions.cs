using BXLogger.Serilog;
using Serilog.Configuration;

// Same convention as the WriteTo extension next door: configuration extensions live in
// `Serilog`, so `using Serilog;` is all a consumer needs.
namespace Serilog;

/// <summary>Adds <c>Destructure.Json()</c> to Serilog's fluent configuration.</summary>
public static class BXLoggerDestructuringExtensions
{
    /// <summary>
    /// Captures <c>System.Text.Json</c> documents and elements as the JSON they hold.
    /// </summary>
    /// <remarks>
    /// Not applied by the sink itself, and it cannot be: destructuring happens when an event
    /// is captured, at the top of the logging pipeline, long before any sink is chosen.
    /// A sink that could fix this would already be too late.
    /// </remarks>
    /// <param name="destructuringConfiguration">The destructuring configuration being extended.</param>
    /// <param name="maxDepth">How deep to descend before falling back to raw text.</param>
    /// <param name="maxItems">How many members or elements to capture per container.</param>
    /// <returns>The logger configuration, for chaining.</returns>
    public static LoggerConfiguration Json(
        this LoggerDestructuringConfiguration destructuringConfiguration,
        int maxDepth = JsonDestructuringPolicy.DefaultMaxDepth,
        int maxItems = JsonDestructuringPolicy.DefaultMaxItems)
    {
        ArgumentNullException.ThrowIfNull(destructuringConfiguration);

        return destructuringConfiguration.With(new JsonDestructuringPolicy(maxDepth, maxItems));
    }
}
