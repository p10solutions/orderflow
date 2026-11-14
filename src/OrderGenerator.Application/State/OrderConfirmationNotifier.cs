using System.Collections.Concurrent;

namespace OrderGenerator.Application.State;

/// <summary>Ephemeral wake-up signals only. Durable state must always be reread after notification.</summary>
public sealed class OrderConfirmationNotifier
{
    private readonly ConcurrentDictionary<(string Id, long Token), TaskCompletionSource> waiters = new();
    private long sequence;
    public int ActiveWaiterCount => waiters.Count;

    public Subscription Subscribe(string id)
    {
        var key = (id, Interlocked.Increment(ref sequence));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters[key] = completion;

        return new(completion.Task, () => waiters.TryRemove(key, out _));
    }

    public void Publish(string id)
    {
        foreach (var item in waiters)
        {
            if (item.Key.Id == id)
            {
                item.Value.TrySetResult();
            }
        }
    }

    public sealed class Subscription(Task completion, Action release) : IDisposable
    {
        public Task Completion => completion;

        public void Dispose() => release();
    }
}
