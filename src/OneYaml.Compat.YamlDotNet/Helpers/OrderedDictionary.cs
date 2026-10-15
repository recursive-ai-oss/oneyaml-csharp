using System;
using System.Collections;
using System.Collections.Generic;

namespace OneYaml.Compat.YamlDotNet.Helpers;

/// <summary>A dictionary that keeps its entries in insertion order and can be read by position.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IOrderedDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Gets or sets the entry at a position.</summary>
    /// <param name="index">The position.</param>
    /// <returns>The entry.</returns>
    KeyValuePair<TKey, TValue> this[int index] { get; set; }

    /// <summary>Inserts an entry at a position.</summary>
    /// <param name="index">The position.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    void Insert(int index, TKey key, TValue value);

    /// <summary>Removes the entry at a position.</summary>
    /// <param name="index">The position.</param>
    void RemoveAt(int index);
}

internal sealed class OrderedDictionary<TKey, TValue> : IOrderedDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly List<KeyValuePair<TKey, TValue>> _entries = [];
    private readonly Dictionary<TKey, int> _index = [];

    public int Count => _entries.Count;

    public bool IsReadOnly => false;

    public ICollection<TKey> Keys => _entries.ConvertAll(e => e.Key);

    public ICollection<TValue> Values => _entries.ConvertAll(e => e.Value);

    public TValue this[TKey key]
    {
        get => _index.TryGetValue(key, out int at) ? _entries[at].Value : throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
        set
        {
            if (_index.TryGetValue(key, out int at))
            {
                _entries[at] = new KeyValuePair<TKey, TValue>(_entries[at].Key, value);
            }
            else
            {
                Add(key, value);
            }
        }
    }

    public KeyValuePair<TKey, TValue> this[int index]
    {
        get => _entries[index];
        set
        {
            KeyValuePair<TKey, TValue> old = _entries[index];
            _index.Remove(old.Key);
            _entries[index] = value;
            _index[value.Key] = index;
        }
    }

    public void Add(TKey key, TValue value)
    {
        if (_index.ContainsKey(key))
        {
            throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
        }

        _index[key] = _entries.Count;
        _entries.Add(new KeyValuePair<TKey, TValue>(key, value));
    }

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public void Insert(int index, TKey key, TValue value)
    {
        if (_index.ContainsKey(key))
        {
            throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
        }

        _entries.Insert(index, new KeyValuePair<TKey, TValue>(key, value));
        Reindex(index);
    }

    public void RemoveAt(int index)
    {
        _index.Remove(_entries[index].Key);
        _entries.RemoveAt(index);
        Reindex(index);
    }

    public bool Remove(TKey key)
    {
        if (!_index.TryGetValue(key, out int at))
        {
            return false;
        }

        RemoveAt(at);
        return true;
    }

    public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);

    public void Clear()
    {
        _entries.Clear();
        _index.Clear();
    }

    public bool ContainsKey(TKey key) => _index.ContainsKey(key);

    public bool Contains(KeyValuePair<TKey, TValue> item) => _index.TryGetValue(item.Key, out int at) && EqualityComparer<TValue>.Default.Equals(_entries[at].Value, item.Value);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_index.TryGetValue(key, out int at))
        {
            value = _entries[at].Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => _entries.CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Reindex(int from)
    {
        for (int i = from; i < _entries.Count; i++)
        {
            _index[_entries[i].Key] = i;
        }
    }
}
