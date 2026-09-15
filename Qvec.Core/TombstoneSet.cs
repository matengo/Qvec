using System.Numerics;
using System.Runtime.CompilerServices;

namespace Qvec.Core
{
    /// <summary>
    /// The set of tombstoned (soft-deleted) row indices, stored as a bitmap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces the <see cref="HashSet{T}"/> that previously held the same state. A
    /// <see cref="HashSet{T}"/> is not safe to read while another thread mutates it: a resize
    /// republishes the bucket array, so a concurrent reader can observe a torn structure, miss an
    /// element that is present, or throw. Search paths read tombstones on every candidate, which
    /// made that an easy race to hit.
    /// </para>
    /// <para>
    /// A bitmap has no such hazard. Each word is read and written with <see cref="Volatile"/>, and
    /// the backing array is allocated once and never resized, so a reader either sees the bit
    /// before a delete or after it — never a corrupted intermediate state. Readers must still not
    /// assume that a sequence of reads is a consistent snapshot; they only need each individual
    /// read to be sane.
    /// </para>
    /// <para>
    /// All mutating members must be called under the database write lock. <see cref="Contains"/>
    /// and <see cref="Count"/> are safe to call without any lock.
    /// </para>
    /// </remarks>
    internal sealed class TombstoneSet
    {
        private const int BitsPerWord = 64;

        private readonly ulong[] _words;
        private readonly int _capacity;
        private int _count;

        internal TombstoneSet(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            _capacity = capacity;
            _words = new ulong[(capacity + BitsPerWord - 1) / BitsPerWord];
        }

        /// <summary>Number of tombstoned indices. Safe to read without a lock.</summary>
        internal int Count => Volatile.Read(ref _count);

        /// <summary>Whether <paramref name="index"/> is tombstoned. Safe to call without a lock.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(int index)
        {
            if ((uint)index >= (uint)_capacity) return false;
            ulong word = Volatile.Read(ref _words[index / BitsPerWord]);
            return (word & (1UL << (index % BitsPerWord))) != 0;
        }

        /// <summary>Tombstones <paramref name="index"/>. Returns false if it already was. Write lock required.</summary>
        internal bool Add(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _capacity);

            int wordIndex = index / BitsPerWord;
            ulong mask = 1UL << (index % BitsPerWord);
            ulong word = _words[wordIndex];
            if ((word & mask) != 0) return false;

            Volatile.Write(ref _words[wordIndex], word | mask);
            Volatile.Write(ref _count, _count + 1);
            return true;
        }

        /// <summary>Clears the tombstone on <paramref name="index"/>. Returns false if it was not set. Write lock required.</summary>
        internal bool Remove(int index)
        {
            if ((uint)index >= (uint)_capacity) return false;

            int wordIndex = index / BitsPerWord;
            ulong mask = 1UL << (index % BitsPerWord);
            ulong word = _words[wordIndex];
            if ((word & mask) == 0) return false;

            Volatile.Write(ref _words[wordIndex], word & ~mask);
            Volatile.Write(ref _count, _count - 1);
            return true;
        }

        /// <summary>Removes every tombstone. Write lock required.</summary>
        internal void Clear()
        {
            for (int i = 0; i < _words.Length; i++)
                Volatile.Write(ref _words[i], 0UL);
            Volatile.Write(ref _count, 0);
        }

        /// <summary>
        /// The lowest tombstoned index, or -1 when the set is empty. Allocating the lowest free
        /// slot first keeps the file as compact as possible.
        /// </summary>
        internal int Min()
        {
            for (int i = 0; i < _words.Length; i++)
            {
                ulong word = Volatile.Read(ref _words[i]);
                if (word != 0)
                    return i * BitsPerWord + BitOperations.TrailingZeroCount(word);
            }
            return -1;
        }
    }
}
