namespace Sample.Lib;

public sealed class Cache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _store = new();

    public void Add(TKey key, TValue value)
    {
        _store[key] = value;
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        return _store.TryGetValue(key, out value);
    }
}
