using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevContext.Infrastructure;

public static class JsonDefaults
{
    /// <summary>
    /// Indented, for JSON a person reads: command output, published schemas, and the configuration
    /// file they are expected to edit.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create(writeIndented: true);

    /// <summary>
    /// Identical apart from indentation, for the artifacts under <c>.dev-context/</c> that only the
    /// tool reads back. Indentation cost between a fifth and a third of every persisted index on
    /// this repository — 33% of <c>symbols.json</c> — which is paid on every write and again on
    /// every cold start that deserializes them.
    ///
    /// Only whitespace differs, so anything written by one is readable by the other and no persisted
    /// contract changes.
    /// </summary>
    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);

    private static JsonSerializerOptions Create(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
