using Xunit;

namespace EngrCAD.Core.Tests;

public class IndexPriorityQueueTests
{
    [Fact]
    public void EnqueueDequeue_ReturnsIdsInPriorityOrder()
    {
        var queue = new IndexPriorityQueue();
        queue.Enqueue(3, 5.0);
        queue.Enqueue(7, 1.0);
        queue.Enqueue(1, 3.0);
        queue.Enqueue(4, 4.0);
        queue.Enqueue(9, 2.0);

        Assert.Equal(5, queue.Count);
        Assert.Equal(7, queue.FirstId);
        Assert.Equal(1.0, queue.FirstPriority);

        Assert.Equal(new[] { 7, 9, 1, 4, 3 }, Drain(queue));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Update_MovesEntriesInBothDirections()
    {
        var queue = new IndexPriorityQueue();
        for (int id = 0; id < 5; id++)
            queue.Enqueue(id, id);

        queue.Update(4, -1.0);    // decrease-key: to the front
        queue.Update(0, 100.0);   // increase-key: to the back
        Assert.Equal(-1.0, queue.PriorityOf(4));

        Assert.Equal(new[] { 4, 1, 2, 3, 0 }, Drain(queue));
    }

    [Fact]
    public void Remove_DeletesOnlyThatEntry()
    {
        var queue = new IndexPriorityQueue();
        for (int id = 0; id < 6; id++)
            queue.Enqueue(id, id * 10.0);

        queue.Remove(2);
        queue.Remove(5);
        Assert.False(queue.Contains(2));
        Assert.True(queue.Contains(3));

        Assert.Equal(new[] { 0, 1, 3, 4 }, Drain(queue));
    }

    [Fact]
    public void EnqueueOrUpdate_KeepsASingleEntryPerId()
    {
        var queue = new IndexPriorityQueue();
        queue.EnqueueOrUpdate(5, 10.0);
        queue.EnqueueOrUpdate(5, 1.0);
        queue.EnqueueOrUpdate(6, 5.0);

        Assert.Equal(2, queue.Count);
        Assert.Equal(1.0, queue.PriorityOf(5));
        Assert.Equal(new[] { 5, 6 }, Drain(queue));
    }

    [Fact]
    public void IdSpace_GrowsOnDemand()
    {
        var queue = new IndexPriorityQueue(initialIdCapacity: 2);
        queue.Enqueue(1000, 2.0);
        queue.Enqueue(1, 1.0);
        queue.Enqueue(999_999, 3.0);

        Assert.True(queue.Contains(1000));
        Assert.False(queue.Contains(500));
        Assert.Equal(new[] { 1, 1000, 999_999 }, Drain(queue));
    }

    [Fact]
    public void InvalidOperations_Throw()
    {
        var queue = new IndexPriorityQueue();
        queue.Enqueue(1, 1.0);

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(1, 2.0));
        Assert.Throws<InvalidOperationException>(() => queue.Update(2, 1.0));
        Assert.Throws<InvalidOperationException>(() => queue.Remove(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(-1, 1.0));

        queue.Dequeue();
        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());
        Assert.Throws<InvalidOperationException>(() => queue.FirstId);
        Assert.False(queue.TryDequeue(out _, out _));
    }

    [Fact]
    public void Clear_EmptiesAndAllowsReuse()
    {
        var queue = new IndexPriorityQueue();
        for (int id = 0; id < 10; id++)
            queue.Enqueue(id, id);
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.Contains(3));
        queue.Enqueue(3, 1.0);
        Assert.Equal(3, queue.Dequeue());
    }

    [Fact]
    public void RandomizedOperations_MatchReferenceModel()
    {
        var random = new Random(12345);
        var queue = new IndexPriorityQueue(initialIdCapacity: 4);
        var model = new Dictionary<int, double>();

        for (int step = 0; step < 5000; step++)
        {
            int op = random.Next(10);
            if (op < 4)
            {
                // Enqueue a fresh id with a distinct priority.
                int id = random.Next(2000);
                if (!model.ContainsKey(id))
                {
                    double priority = step + random.NextDouble();
                    model[id] = priority;
                    queue.Enqueue(id, priority);
                }
            }
            else if (op < 6 && model.Count > 0)
            {
                int id = RandomKey(model, random);
                double priority = step + random.NextDouble() - random.Next(2) * 1000.0;
                model[id] = priority;
                queue.Update(id, priority);
            }
            else if (op < 7 && model.Count > 0)
            {
                int id = RandomKey(model, random);
                model.Remove(id);
                queue.Remove(id);
            }
            else if (model.Count > 0)
            {
                double expected = model.Values.Min();
                Assert.True(queue.TryDequeue(out int id, out double priority));
                Assert.Equal(expected, priority);
                Assert.Equal(expected, model[id]);
                model.Remove(id);
            }
            Assert.Equal(model.Count, queue.Count);
        }

        // Drain and verify full ascending order against the model.
        double last = double.NegativeInfinity;
        while (queue.TryDequeue(out int id, out double priority))
        {
            Assert.True(priority >= last);
            Assert.Equal(model[id], priority);
            model.Remove(id);
            last = priority;
        }
        Assert.Empty(model);
    }

    private static int RandomKey(Dictionary<int, double> model, Random random)
    {
        int index = random.Next(model.Count);
        foreach (int key in model.Keys)
        {
            if (index-- == 0)
                return key;
        }
        throw new InvalidOperationException();
    }

    private static List<int> Drain(IndexPriorityQueue queue)
    {
        var ids = new List<int>();
        double last = double.NegativeInfinity;
        while (queue.TryDequeue(out int id, out double priority))
        {
            Assert.True(priority >= last, "dequeue order must be ascending");
            Assert.False(queue.Contains(id));
            ids.Add(id);
            last = priority;
        }
        return ids;
    }
}
