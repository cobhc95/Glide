namespace Glide.Core.Workspace;

/// <summary>
/// 5.1.0 instant tab switching: a small most-recently-used store of per-tab state that lets a tab be shown again
/// without reloading it (image tabs: a display-sized frame + the folder list; Explorer tabs: the folder listing).
/// Framework-free and single-threaded (UI thread). Values that own resources are handed back through the
/// <c>evicted</c> callback when they leave the cache (capacity, replacement, removal or tab close) exactly once.
/// </summary>
public sealed class TabWarmCache<TValue>
{
    private readonly LinkedList<(Guid Id, TValue Value)> _order = new();
    private readonly Dictionary<Guid, LinkedListNode<(Guid Id, TValue Value)>> _nodes = new();
    private readonly Action<TValue>? _evicted;

    public TabWarmCache(int capacity, Action<TValue>? evicted = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _evicted = evicted;
    }

    public int Capacity { get; }
    public int Count => _nodes.Count;

    /// <summary>Most recent first.</summary>
    public IEnumerable<Guid> Ids => _order.Select(node => node.Id);

    public bool Contains(Guid id) => _nodes.ContainsKey(id);

    /// <summary>Looks a tab up and marks it most recently used.</summary>
    public bool TryGet(Guid id, out TValue value)
    {
        if (_nodes.TryGetValue(id, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Stores (or replaces) a tab's value as most recently used; the least recently used tabs beyond the capacity
    /// are evicted. A replaced value is evicted too unless <paramref name="sameResource"/> says it is the same one.
    /// </summary>
    public void Put(Guid id, TValue value, Func<TValue, TValue, bool>? sameResource = null)
    {
        if (_nodes.Remove(id, out var existing))
        {
            _order.Remove(existing);
            if (sameResource is null || !sameResource(existing.Value.Value, value)) _evicted?.Invoke(existing.Value.Value);
        }
        _nodes[id] = _order.AddFirst((id, value));
        while (_nodes.Count > Capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _nodes.Remove(last.Value.Id);
            _evicted?.Invoke(last.Value.Value);
        }
    }

    public bool Remove(Guid id)
    {
        if (!_nodes.Remove(id, out var node)) return false;
        _order.Remove(node);
        _evicted?.Invoke(node.Value.Value);
        return true;
    }

    /// <summary>Drops every tab that is no longer open (closed, moved to another window).</summary>
    public int RetainOnly(IEnumerable<Guid> openTabIds)
    {
        var open = openTabIds as ISet<Guid> ?? new HashSet<Guid>(openTabIds);
        var gone = _nodes.Keys.Where(id => !open.Contains(id)).ToList();
        foreach (var id in gone) Remove(id);
        return gone.Count;
    }

    public void Clear()
    {
        var values = _order.Select(node => node.Value).ToList();
        _order.Clear();
        _nodes.Clear();
        foreach (var value in values) _evicted?.Invoke(value);
    }
}
