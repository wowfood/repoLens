namespace DevContext.Services;

internal static class ParallelWork
{
    public static async Task<IReadOnlyList<TResult>> SelectAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maxParallelism,
        Func<TSource, CancellationToken, Task<TResult>> selector,
        CancellationToken cancellationToken)
    {
        var items = source.ToArray();
        if (items.Length == 0)
        {
            return [];
        }

        var results = new TResult[items.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = cancellationToken
            },
            async (index, token) => results[index] = await selector(items[index], token));
        return results;
    }
}
