using System.Collections.Concurrent;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

/// <summary>
/// One file's stat and content hash. <paramref name="Path"/> is repository-relative: the table is a
/// third of its size that way, and a repository that is moved or checked out elsewhere keeps a
/// usable table instead of silently missing on every entry.
/// </summary>
internal sealed record FileFingerprint(string Path, long Length, long ModifiedTicks, string Hash);

internal sealed record FileFingerprintTable
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required IReadOnlyList<FileFingerprint> Files { get; init; }
}

/// <summary>
/// Content hashes for repository input files, keyed by the file's size and modification time.
///
/// Every call had to SHA-256 the full content of every repository input before it could even consult
/// the in-memory graph cache, serially, which made the cost of asking a question scale with the size
/// of the repository rather than with the size of the edit. On a 5,000-file repository that was most
/// of a warm call.
///
/// The persisted cache key is still the content hash, so what the graph cache is keyed on has not
/// changed and two runs over identical content still agree. What has changed is how the content hash
/// is obtained: a file whose length and modification time both match the recorded ones is assumed to
/// have the recorded content. That assumption is why this is only consulted when caching is enabled
/// — the switch a caller already uses to trade freshness for speed — and it is wrong only for an
/// edit that preserves a file's exact byte length and lands inside its filesystem's timestamp
/// granularity. Deleting <c>.dev-context/fingerprints.json</c>, or disabling the cache, forces a full
/// re-read.
/// </summary>
internal sealed class FileFingerprintCache
{
    private readonly ConcurrentDictionary<string, FileFingerprint> byPath =
        new(StringComparer.OrdinalIgnoreCase);

    private int hits;
    private int misses;
    private bool loaded;
    private bool dirty;

    public int Hits => Volatile.Read(ref hits);

    public int Misses => Volatile.Read(ref misses);

    public static string PathFor(string repositoryRoot) => ContextPaths.Fingerprints(repositoryRoot);

    public async Task LoadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        var path = PathFor(repositoryRoot);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var table = await JsonFile.ReadAsync<FileFingerprintTable>(path, cancellationToken);
            if (!SchemaVersions.IsReadable(table.SchemaVersion))
            {
                return;
            }

            foreach (var fingerprint in table.Files)
            {
                var full = RepositoryFileFilter.ToFullPath(repositoryRoot, fingerprint.Path);
                byPath[full] = fingerprint with { Path = full };
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or System.Text.Json.JsonException)
        {
            // A fingerprint table is an optimization with no correct-but-stale state worth
            // recovering: an unreadable one is simply a cold start, never a failed command.
            byPath.Clear();
        }
    }

    /// <summary>
    /// The content hash of a file, read from disk only when its size or modification time differs
    /// from the recorded one.
    /// </summary>
    public async Task<string> HashAsync(string fullPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        if (byPath.TryGetValue(fullPath, out var known)
            && known.Length == length
            && known.ModifiedTicks == modified)
        {
            Interlocked.Increment(ref hits);
            return known.Hash;
        }

        Interlocked.Increment(ref misses);
        var hash = await Hashing.FileAsync(fullPath, cancellationToken);
        byPath[fullPath] = new FileFingerprint(fullPath, length, modified, hash);
        dirty = true;
        return hash;
    }

    public async Task SaveAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        if (!dirty)
        {
            return;
        }

        dirty = false;
        try
        {
            await JsonFile.WriteAsync(
                PathFor(repositoryRoot),
                new FileFingerprintTable
                {
                    Files = byPath.Values
                        .Select(fingerprint => fingerprint with
                        {
                            Path = Path.GetRelativePath(repositoryRoot, fingerprint.Path)
                                .Replace(Path.DirectorySeparatorChar, '/')
                        })
                        .OrderBy(fingerprint => fingerprint.Path, StringComparer.Ordinal)
                        .ToArray()
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Two processes racing the write, or a read-only checkout. Losing the table costs a
            // slower next run and nothing else.
        }
    }
}
