using System.Runtime.CompilerServices;

namespace Qvec.Extensions.VectorData.Tests;

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (T item in source.ConfigureAwait(false))
        {
            list.Add(item);
        }

        return list;
    }
}
