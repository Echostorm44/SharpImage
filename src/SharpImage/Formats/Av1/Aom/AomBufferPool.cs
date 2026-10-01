using System.Collections.Concurrent;

namespace SharpImage.Formats.Av1;

/// <summary>Reuse of the encoder's large per-frame arrays (frame planes, the mi grid, the post filters' copies): a fresh
/// large array is a large-object-heap allocation whose zeroing, page commits and gen-2 collections cost more than the
/// encode of a small frame at the fast speeds. Rented arrays are cleared, so they read as freshly allocated ones;
/// returning is optional (an array never returned is collected as usual) and must only happen once nothing uses it.</summary>
internal static class AomBufferPool
{
    // below this many bytes an array is a cheap small-object allocation: not pooled
    private const int MinBytes = 64 * 1024;
    // at most this many arrays kept per (type, length)
    private const int MaxPerKey = 16;
    private static readonly ConcurrentDictionary<(Type, int), ConcurrentBag<Array>> Pools = new();

    private static bool Pooled<T>(int length) => (long)length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() >= MinBytes;

    /// <summary>A zeroed T[length] (pooled or new).</summary>
    internal static T[] Rent<T>(int length)
    {
        if (Pooled<T>(length) && Pools.TryGetValue((typeof(T), length), out var bag) && bag.TryTake(out var a))
        {
            Array.Clear(a);
            return (T[])a;
        }
        return new T[length];
    }

    /// <summary>Gives the array back for reuse (null: nothing).</summary>
    internal static void Return<T>(T[]? a)
    {
        if (a == null || !Pooled<T>(a.Length)) return;
        var bag = Pools.GetOrAdd((typeof(T), a.Length), _ => new ConcurrentBag<Array>());
        if (bag.Count < MaxPerKey) bag.Add(a);
    }
}
