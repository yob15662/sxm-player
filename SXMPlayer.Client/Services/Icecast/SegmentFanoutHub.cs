using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Fans out shared HLS segments to all connected Icecast client queues.
/// </summary>
public sealed class SegmentFanoutHub
{
    private readonly ConcurrentDictionary<Guid, (SXMListener Listener, SubscriberRegistration Registration)> _subscribers = new();
    private readonly Action _onEmpty;

    public SegmentFanoutHub(Action onEmpty)
    {
        _onEmpty = onEmpty ?? throw new ArgumentNullException(nameof(onEmpty));
    }

    public bool HasSubscribers => !_subscribers.IsEmpty;

    public int SubscriberCount => _subscribers.Values.Select(v => v.Listener).Distinct().Count();

    public void Register(SXMListener listener, ChannelWriter<SegmentWorkItem> writer, CancellationToken disconnectToken)
    {
        if (listener is null)
        {
            throw new ArgumentNullException(nameof(listener));
        }

        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        if (disconnectToken.IsCancellationRequested)
        {
            writer.TryComplete();
            return;
        }

        var subscriptionId = Guid.NewGuid();
        var subscriber = new SubscriberRegistration(writer, disconnectToken.Register(() => Unregister(subscriptionId)));

        _subscribers[subscriptionId] = (listener, subscriber);
    }

    public async ValueTask BroadcastAsync(SegmentWorkItem item, CancellationToken cancellationToken)
    {
        foreach (var (subscriptionId, (listener, subscriber)) in _subscribers.ToArray())
        {
            try
            {
                await subscriber.Writer.WriteAsync(item, cancellationToken);
            }
            catch (ChannelClosedException)
            {
                Unregister(subscriptionId);
            }
        }
    }

    public void Unregister(SXMListener listener, Exception? error = null)
    {
        var subscriptionsToRemove = _subscribers
            .Where(kvp => kvp.Value.Listener.Equals(listener))
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (var subscriptionId in subscriptionsToRemove)
        {
            Unregister(subscriptionId, error);
        }
    }

    private void Unregister(Guid subscriptionId, Exception? error = null)
    {
        if (_subscribers.TryRemove(subscriptionId, out var entry))
        {
            entry.Registration.Complete(error);
            if (_subscribers.IsEmpty)
            {
                _onEmpty();
            }
        }
    }

    public void CompleteAll(Exception? error = null)
    {
        foreach (var subscriptionId in _subscribers.Keys.ToArray())
        {
            Unregister(subscriptionId, error);
        }
    }

    private sealed class SubscriberRegistration
    {
        public SubscriberRegistration(ChannelWriter<SegmentWorkItem> writer, CancellationTokenRegistration registration)
        {
            Writer = writer;
            Registration = registration;
        }

        public ChannelWriter<SegmentWorkItem> Writer { get; }

        private CancellationTokenRegistration Registration { get; }

        public void Complete(Exception? error = null)
        {
            Registration.Dispose();
            Writer.TryComplete(error);
        }
    }
}
