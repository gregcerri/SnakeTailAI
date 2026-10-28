using System.Collections;

namespace SnakeTailAI.Core;

/// <summary>
/// Append-only list stored in fixed-size chunks. Items never move, so one writer thread can append
/// while other threads read indices below a <see cref="Count"/> they have already seen.
/// It also avoids huge array reallocations with millions of lines.
/// </summary>
public sealed class ChunkedList<T> : IReadOnlyList<T>
{
    private const int Shift = 14;
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private T[][] _chunks = new T[8][];
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _chunks[index >> Shift][index & Mask];
        }
    }

    public void Add(T item)
    {
        int count = _count;
        int chunk = count >> Shift;
        if (chunk == _chunks.Length)
        {
            var grown = new T[_chunks.Length * 2][];
            Array.Copy(_chunks, grown, _chunks.Length);
            _chunks = grown;
        }
        (_chunks[chunk] ??= new T[ChunkSize])[count & Mask] = item;
        Volatile.Write(ref _count, count + 1);
    }

    public IEnumerator<T> GetEnumerator()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
            yield return _chunks[i >> Shift][i & Mask];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
