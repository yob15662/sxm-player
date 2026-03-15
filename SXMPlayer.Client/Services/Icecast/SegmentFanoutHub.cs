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
    private readonly ConcurrentDictionary<SXMListener, SubscriberRegistration> _subscribers = new();
    private readonly Action _onEmpty;

    public SegmentFanoutHub(Action onEmpty)
    {
        _onEmpty = onEmpty ?? throw new ArgumentNullException(nameof(onEmpty));
    }

    public bool HasSubscribers => !_subscribers.IsEmpty;

    public int SubscriberCount => _subscribers.Count;

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

        var subscriber = new SubscriberRegistration(writer, disconnectToken.Register(() => Unregister(listener)));

        while (true)
        {
            if (_subscribers.TryGetValue(listener, out var existing))
            {
                if (_subscribers.TryUpdate(listener, subscriber, existing))
                {
                    existing.Complete();
                    return;
                }

                continue;
            }

            if (_subscribers.TryAdd(listener, subscriber))
            {
                return;
            }
        }
    }

    public async ValueTask BroadcastAsync(SegmentWorkItem item, CancellationToken cancellationToken)
    {
        foreach (var (listener, subscriber) in _subscribers.ToArray())
        {
            try
            {
                await subscriber.Writer.WriteAsync(item, cancellationToken);
            }
            catch (ChannelClosedException)
            {
                Unregister(listener);
            }
        }
    }

    public void Unregister(SXMListener listener, Exception? error = null)
    {
        if (_subscribers.TryRemove(listener, out var subscriber))
        {
            subscriber.Complete(error);
            if (_subscribers.IsEmpty)
            {
                _onEmpty();
            }
        }
    }

    public void CompleteAll(Exception? error = null)
    {
        foreach (var listener in _subscribers.Keys.ToArray())
        {
            Unregister(listener, error);
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
