#pragma warning disable MA0006
#pragma warning disable MA0008

#nullable disable

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Jint.Collections;

/// <summary>
/// DictionarySlim&lt;string, TValue> is similar to Dictionary&lt;TKey, TValue> but optimized in three ways:
/// 1) It allows access to the value by ref replacing the common TryGetValue and Add pattern.
/// 2) It does not store the hash code (assumes it is cheap to equate values).
/// 3) It does not accept an equality comparer (assumes Object.GetHashCode() and Object.Equals() or overridden implementation are cheap and sufficient).
/// </summary>
[DebuggerTypeProxy(typeof(DictionarySlimDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class StringDictionarySlim<TValue> : DictionaryBase<TValue>, IReadOnlyCollection<KeyValuePair<Key, TValue>>
{
    // We want to initialize without allocating arrays. We also want to avoid null checks.
    // Array.Empty would give divide by zero in modulo operation. So we use static one element arrays.
    // The first add will cause a resize replacing these with real arrays of three elements.
    // Arrays are wrapped in a class to avoid being duplicated for each <TKey, TValue>
    private static readonly Entry[] InitialEntries = new Entry[1];
    private int _count;
    // 0-based index into _entries of head of free chain: -1 means empty
    private int _freeList = -1;
    // 1-based index into _entries; 0 means empty
    private int[] _buckets;
    private Entry[] _entries;

    [DebuggerDisplay("({key}, {value})->{next}")]
    private struct Entry
    {
        public Key key;
        public TValue value;
        // 0-based index of next entry in chain: -1 means end of chain
        // also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
        // so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
        public int next;
    }

    public StringDictionarySlim()
    {
        _buckets = HashHelpers.SizeOneIntArray;
        _entries = InitialEntries;
    }

    public StringDictionarySlim(int capacity)
    {
        if (capacity < 2)
            capacity = 2; // 1 would indicate the dummy array
        capacity = HashHelpers.PowerOf2(capacity);
        _buckets = new int[capacity];
        _entries = new Entry[capacity];
    }

    public override int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>
    /// Clears the dictionary. Note that this invalidates any active enumerators.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _freeList = -1;
        _buckets = HashHelpers.SizeOneIntArray;
        _entries = InitialEntries;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetOrUpdateValue<TState>(Key key, Func<TValue, TState, TValue> updater, TState state)
    {
        ref var currentValue = ref GetValueRefOrAddDefault(key, out _);
        currentValue = updater(currentValue, state);
    }

    public bool Remove(Key key)
    {
        Entry[] entries = _entries;
        int bucketIndex = key.HashCode & (_buckets.Length - 1);
        int entryIndex = _buckets[bucketIndex] - 1;

        int lastIndex = -1;
        while (entryIndex != -1)
        {
            Entry candidate = entries[entryIndex];
            if (candidate.key == key)
            {
                if (lastIndex != -1)
                {   // Fixup preceding element in chain to point to next (if any)
                    entries[lastIndex].next = candidate.next;
                }
                else
                {   // Fixup bucket to new head (if any)
                    _buckets[bucketIndex] = candidate.next + 1;
                }

                entries[entryIndex] = default;

                entries[entryIndex].next = -3 - _freeList; // New head of free list
                _freeList = entryIndex;

                _count--;
                return true;
            }
            lastIndex = entryIndex;
            entryIndex = candidate.next;
        }

        return false;
    }

    public override ref TValue GetValueRefOrNullRef(Key key)
    {
        Entry[] entries = _entries;
        int bucketIndex = key.HashCode & (_buckets.Length - 1);
        for (int i = _buckets[bucketIndex] - 1;
             (uint) i < (uint) entries.Length; i = entries[i].next)
        {
            if (key.Name == entries[i].key.Name)
            {
                return ref entries[i].value;
            }
        }

        return ref Unsafe.NullRef<TValue>();
    }


    // Not safe for concurrent _reads_ (at least, if either of them add)
    // For concurrent reads, prefer TryGetValue(key, out value)
    /// <summary>
    /// Gets the value for the specified key, or, if the key is not present,
    /// adds an entry and returns the value by ref. This makes it possible to
    /// add or update a value in a single look up operation.
    /// </summary>
    /// <param name="key">Key to look for</param>
    /// <param name="exists">Whether the value existed</param>
    /// <returns>Reference to the new or existing value</returns>
    public override ref TValue GetValueRefOrAddDefault(Key key, out bool exists)
    {
        Entry[] entries = _entries;
        int bucketIndex = key.HashCode & (_buckets.Length - 1);
        for (int i = _buckets[bucketIndex] - 1;
             (uint) i < (uint) entries.Length; i = entries[i].next)
        {
            if (key.Name == entries[i].key.Name)
            {
                exists = true;
                return ref entries[i].value;
            }
        }

        exists = false;
        return ref AddKey(key, bucketIndex);
    }

    public bool TryAdd(Key key, TValue value)
    {
        Entry[] entries = _entries;
        int bucketIndex = key.HashCode & (_buckets.Length - 1);
        for (int i = _buckets[bucketIndex] - 1;
             (uint) i < (uint) entries.Length; i = entries[i].next)
        {
            if (key.Name == entries[i].key.Name)
            {
                return false;
            }
        }

        AddKey(key, bucketIndex) = value;
        return true;
    }

    /// <summary>
    /// Adds a new item and expects key to not exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddDangerous(in Key key, TValue value)
    {
        AddKey(key, key.HashCode & (_buckets.Length - 1)) = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref TValue AddKey(Key key, int bucketIndex)
    {
        Entry[] entries = _entries;
        int entryIndex;
        if (_freeList != -1)
        {
            entryIndex = _freeList;
            _freeList = -3 - entries[_freeList].next;
        }
        else
        {
            if (_count == entries.Length || entries.Length == 1)
            {
                entries = Resize();
                bucketIndex = key.HashCode & (_buckets.Length - 1);
                // entry indexes were not changed by Resize
            }
            entryIndex = _count;
        }

        entries[entryIndex].key = key;
        entries[entryIndex].next = _buckets[bucketIndex] - 1;
        _buckets[bucketIndex] = entryIndex + 1;
        _count++;
        return ref entries[entryIndex].value;
    }

    private Entry[] Resize()
    {
        Debug.Assert(_entries.Length == _count || _entries.Length == 1); // We only copy _count, so if it's longer we will miss some
        int count = _count;
        int newSize = _entries.Length * 2;
        if ((uint) newSize > int.MaxValue) // uint cast handles overflow
            throw new InvalidOperationException("Capacity Overflow");

        var entries = new Entry[newSize];
        Array.Copy(_entries, 0, entries, 0, count);

        var newBuckets = new int[entries.Length];
        while (count-- > 0)
        {
            int bucketIndex = entries[count].key.HashCode & (newBuckets.Length - 1);
            entries[count].next = newBuckets[bucketIndex] - 1;
            newBuckets[bucketIndex] = count + 1;
        }

        _buckets = newBuckets;
        _entries = entries;

        return entries;
    }

    /// <summary>
    /// Gets an enumerator over the dictionary
    /// </summary>
    public Enumerator GetEnumerator() => new Enumerator(this); // avoid boxing

    /// <summary>
    /// Gets an enumerator over the dictionary
    /// </summary>
    IEnumerator<KeyValuePair<Key, TValue>> IEnumerable<KeyValuePair<Key, TValue>>.GetEnumerator() =>
        new Enumerator(this);

    /// <summary>
    /// Gets an enumerator over the dictionary
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    public struct Enumerator : IEnumerator<KeyValuePair<Key, TValue>>
    {
        private readonly StringDictionarySlim<TValue> _dictionary;
        private int _index;
        private int _count;
        private KeyValuePair<Key, TValue> _current;

        internal Enumerator(StringDictionarySlim<TValue> dictionary)
        {
            _dictionary = dictionary;
            _index = 0;
            _count = _dictionary._count;
            _current = default;
        }

        public bool MoveNext()
        {
            if (_count == 0)
            {
                _current = default;
                return false;
            }

            _count--;

            while (_dictionary._entries[_index].next < -1)
                _index++;

            _current = new KeyValuePair<Key, TValue>(
                _dictionary._entries[_index].key,
                _dictionary._entries[_index++].value);
            return true;
        }

        public KeyValuePair<Key, TValue> Current => _current;

        object IEnumerator.Current => _current;

        void IEnumerator.Reset()
        {
            _index = 0;
            _count = _dictionary._count;
            _current = default;
        }

        public void Dispose() { }
    }

    internal static class HashHelpers
    {
        internal static readonly int[] SizeOneIntArray = new int[1];

        internal static int PowerOf2(int v)
        {
            if ((v & (v - 1)) == 0) return v;
            int i = 2;
            while (i < v) i <<= 1;
            return i;
        }
    }

    internal sealed class DictionarySlimDebugView<V>
    {
        private readonly StringDictionarySlim<V> _dictionary;

        public DictionarySlimDebugView(StringDictionarySlim<V> dictionary)
        {
            _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public KeyValuePair<Key, V>[] Items => _dictionary.ToArray();
    }
}
