using System.Text.Json;

namespace DevContext.Infrastructure;

internal static class JsonFile
{
    /// <summary>Writes a stored artifact that only the tool reads back, without indentation.</summary>
    public static Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        WriteAsync(path, value, JsonDefaults.Compact, cancellationToken);

    /// <summary>
    /// Writes a file a person is expected to open — the configuration they edit, or a metrics file
    /// sitting beside a Markdown report — with indentation.
    /// </summary>
    public static Task WriteReadableAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        WriteAsync(path, value, JsonDefaults.Options, cancellationToken);

    private static async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path has no parent directory: {path}");
        Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }

        File.Move(temporaryPath, path, true);
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, cancellationToken)
               ?? throw new InvalidOperationException($"Stored JSON is empty or invalid: {path}");
    }
}
