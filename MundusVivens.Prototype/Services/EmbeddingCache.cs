using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IEmbeddingCache
{
    Task<float[]> GetOrComputeEmbeddingAsync(string text, Func<string, Task<float[]>> computeFunc);
}

public class EmbeddingCache : IEmbeddingCache
{
    private readonly ConcurrentDictionary<string, AsyncLazy<float[]>> _cache = new();

    public async Task<float[]> GetOrComputeEmbeddingAsync(string text, Func<string, Task<float[]>> computeFunc)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();

        var lazy = _cache.GetOrAdd(text, t => new AsyncLazy<float[]>(() => computeFunc(t)));
        return await lazy.Value;
    }

    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0)
            return 0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

public class AsyncLazy<T> : Lazy<Task<T>>
{
    public AsyncLazy(Func<Task<T>> taskFactory) :
        base(() => Task.Factory.StartNew(taskFactory).Unwrap())
    { }
}
