namespace EngrCAD.Core;

/// <summary>
/// Array-backed binary min-heap keyed by non-negative integer ids, with an O(1)
/// id → heap-slot index so <see cref="Update"/> (decrease/increase-key),
/// <see cref="Remove"/>, and <see cref="Contains"/> are O(log n) / O(1) instead of the
/// lazy duplicate-entry workaround <c>PriorityQueue&lt;T, T&gt;</c> forces. Each id may
/// be present at most once; re-prioritizing an id moves its single entry, so the queue
/// never holds stale duplicates. Storage is struct-of-arrays and grows on demand — the
/// id space does not need to be declared up front (unlike geometry3Sharp's
/// <c>IndexPriorityQueue</c>, which this is modeled on).
/// </summary>
/// <remarks>
/// Lower priority dequeues first; ties dequeue in unspecified order. Not thread-safe.
/// Typical use: mesh decimation (edge-collapse candidates keyed by edge id) and other
/// greedy algorithms whose keys change as the structure evolves.
/// </remarks>
public sealed class IndexPriorityQueue
{
    // 1-based heap (slot 0 unused) over parallel arrays.
    private int[] _heapIds;
    private double[] _heapPriorities;
    private int[] _idToSlot; // -1 = not in queue; grows to cover the largest id seen
    private int _count;

    public IndexPriorityQueue(int initialIdCapacity = 16)
    {
        if (initialIdCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialIdCapacity));
        _heapIds = new int[16];
        _heapPriorities = new double[16];
        _idToSlot = new int[Math.Max(1, initialIdCapacity)];
        Array.Fill(_idToSlot, -1);
    }

    /// <summary>Number of ids currently in the queue.</summary>
    public int Count => _count;

    /// <summary>The id with the smallest priority. Throws if empty.</summary>
    public int FirstId
    {
        get
        {
            ThrowIfEmpty();
            return _heapIds[1];
        }
    }

    /// <summary>The smallest priority in the queue. Throws if empty.</summary>
    public double FirstPriority
    {
        get
        {
            ThrowIfEmpty();
            return _heapPriorities[1];
        }
    }

    /// <summary>O(1) check whether <paramref name="id"/> is currently in the queue.</summary>
    public bool Contains(int id) =>
        (uint)id < (uint)_idToSlot.Length && _idToSlot[id] > 0;

    /// <summary>Adds <paramref name="id"/> with the given priority. Throws if already present.</summary>
    public void Enqueue(int id, double priority)
    {
        if (id < 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Ids must be non-negative.");
        if (Contains(id))
            throw new InvalidOperationException($"Id {id} is already in the queue; use Update or EnqueueOrUpdate.");

        EnsureIdCapacity(id);
        if (++_count == _heapIds.Length)
        {
            Array.Resize(ref _heapIds, _heapIds.Length * 2);
            Array.Resize(ref _heapPriorities, _heapPriorities.Length * 2);
        }
        _heapIds[_count] = id;
        _heapPriorities[_count] = priority;
        _idToSlot[id] = _count;
        SiftUp(_count);
    }

    /// <summary>Adds the id, or re-prioritizes its existing single entry if already queued.</summary>
    public void EnqueueOrUpdate(int id, double priority)
    {
        if (Contains(id))
            Update(id, priority);
        else
            Enqueue(id, priority);
    }

    /// <summary>Removes and returns the id with the smallest priority. Throws if empty.</summary>
    public int Dequeue()
    {
        ThrowIfEmpty();
        int id = _heapIds[1];
        RemoveAtSlot(1);
        return id;
    }

    /// <summary>Removes the smallest-priority id, if any.</summary>
    public bool TryDequeue(out int id, out double priority)
    {
        if (_count == 0)
        {
            id = -1;
            priority = 0;
            return false;
        }
        id = _heapIds[1];
        priority = _heapPriorities[1];
        RemoveAtSlot(1);
        return true;
    }

    /// <summary>Removes <paramref name="id"/> from the queue. Throws if not present.</summary>
    public void Remove(int id)
    {
        int slot = SlotOf(id);
        RemoveAtSlot(slot);
    }

    /// <summary>Changes the priority of a queued id (either direction) and re-heapifies. Throws if not present.</summary>
    public void Update(int id, double priority)
    {
        int slot = SlotOf(id);
        double old = _heapPriorities[slot];
        _heapPriorities[slot] = priority;
        if (priority < old)
            SiftUp(slot);
        else
            SiftDown(slot);
    }

    /// <summary>The current priority of a queued id. Throws if not present.</summary>
    public double PriorityOf(int id) => _heapPriorities[SlotOf(id)];

    /// <summary>Empties the queue, keeping internal storage for reuse.</summary>
    public void Clear()
    {
        for (int slot = 1; slot <= _count; slot++)
            _idToSlot[_heapIds[slot]] = -1;
        _count = 0;
    }

    private int SlotOf(int id)
    {
        if (!Contains(id))
            throw new InvalidOperationException($"Id {id} is not in the queue.");
        return _idToSlot[id];
    }

    private void ThrowIfEmpty()
    {
        if (_count == 0)
            throw new InvalidOperationException("The queue is empty.");
    }

    private void EnsureIdCapacity(int id)
    {
        if (id < _idToSlot.Length)
            return;
        int oldLength = _idToSlot.Length;
        int newLength = Math.Max(oldLength * 2, id + 1);
        Array.Resize(ref _idToSlot, newLength);
        Array.Fill(_idToSlot, -1, oldLength, newLength - oldLength);
    }

    private void RemoveAtSlot(int slot)
    {
        _idToSlot[_heapIds[slot]] = -1;
        if (slot == _count)
        {
            _count--;
            return;
        }
        // Move the last entry into the hole and re-heapify in whichever direction.
        Move(_count, slot);
        _count--;
        int movedId = _heapIds[slot];
        SiftUp(slot);
        if (_idToSlot[movedId] == slot)
            SiftDown(slot);
    }

    private void Move(int fromSlot, int toSlot)
    {
        _heapIds[toSlot] = _heapIds[fromSlot];
        _heapPriorities[toSlot] = _heapPriorities[fromSlot];
        _idToSlot[_heapIds[toSlot]] = toSlot;
    }

    private void SiftUp(int slot)
    {
        int id = _heapIds[slot];
        double priority = _heapPriorities[slot];
        while (slot > 1)
        {
            int parent = slot >> 1;
            if (_heapPriorities[parent] <= priority)
                break;
            Move(parent, slot);
            slot = parent;
        }
        _heapIds[slot] = id;
        _heapPriorities[slot] = priority;
        _idToSlot[id] = slot;
    }

    private void SiftDown(int slot)
    {
        int id = _heapIds[slot];
        double priority = _heapPriorities[slot];
        while (true)
        {
            int child = slot << 1;
            if (child > _count)
                break;
            if (child + 1 <= _count && _heapPriorities[child + 1] < _heapPriorities[child])
                child++;
            if (_heapPriorities[child] >= priority)
                break;
            Move(child, slot);
            slot = child;
        }
        _heapIds[slot] = id;
        _heapPriorities[slot] = priority;
        _idToSlot[id] = slot;
    }
}
