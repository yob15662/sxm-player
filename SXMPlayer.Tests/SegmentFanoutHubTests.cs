using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SXMPlayer.Tests;

public class SegmentFanoutHubTests
{
    private static SXMListener CreateListener(string ipAddress)
    {
        return new SXMListener(IPAddress.Parse(ipAddress));
    }

    private static SegmentWorkItem CreateItem()
    {
        return new SegmentWorkItem("segment.aac", "v1", 123, new Memory<byte>(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task BroadcastAsync_WithMultipleSubscribers_WritesToEveryChannel()
    {
        var onEmptyCalls = 0;
        var hub = new SegmentFanoutHub(() => onEmptyCalls++);
        var listener1 = CreateListener("127.0.0.1");
        var listener2 = CreateListener("127.0.0.2");
        var channel1 = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var channel2 = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();

        hub.Register(listener1, channel1.Writer, CancellationToken.None);
        hub.Register(listener2, channel2.Writer, CancellationToken.None);

        await hub.BroadcastAsync(CreateItem(), CancellationToken.None);

        var item1 = await channel1.Reader.ReadAsync();
        var item2 = await channel2.Reader.ReadAsync();

        Assert.Equal("segment.aac", item1.SegmentName);
        Assert.Equal("segment.aac", item2.SegmentName);
        Assert.Equal(0, onEmptyCalls);
    }

    [Fact]
    public async Task Register_WithSameListener_ReplacesPreviousChannel()
    {
        var hub = new SegmentFanoutHub(() => { });
        var listener = CreateListener("127.0.0.10");
        var firstChannel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var secondChannel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();

        hub.Register(listener, firstChannel.Writer, CancellationToken.None);
        hub.Register(listener, secondChannel.Writer, CancellationToken.None);

        await Assert.ThrowsAsync<ChannelClosedException>(() => firstChannel.Reader.ReadAsync().AsTask());

        await hub.BroadcastAsync(CreateItem(), CancellationToken.None);

        var item = await secondChannel.Reader.ReadAsync();
        Assert.Equal("segment.aac", item.SegmentName);
    }

    [Fact]
    public async Task Unregister_WhenLastSubscriberRemoved_CompletesChannelAndInvokesOnEmpty()
    {
        var onEmptyCalls = 0;
        var hub = new SegmentFanoutHub(() => onEmptyCalls++);
        var listener = CreateListener("127.0.0.20");
        var channel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        using var disconnect = new CancellationTokenSource();

        hub.Register(listener, channel.Writer, disconnect.Token);
        disconnect.Cancel();

        await Assert.ThrowsAsync<ChannelClosedException>(() => channel.Reader.ReadAsync().AsTask());
        Assert.Equal(1, onEmptyCalls);
        Assert.False(hub.HasSubscribers);
    }

    [Fact]
    public async Task CompleteAll_WithError_FaultsSubscriberChannels()
    {
        var hub = new SegmentFanoutHub(() => { });
        var listener1 = CreateListener("127.0.0.30");
        var listener2 = CreateListener("127.0.0.31");
        var channel1 = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var channel2 = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var error = new InvalidOperationException("boom");

        hub.Register(listener1, channel1.Writer, CancellationToken.None);
        hub.Register(listener2, channel2.Writer, CancellationToken.None);

        hub.CompleteAll(error);

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel1.Reader.Completion);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel2.Reader.Completion);
        Assert.Equal("boom", ex1.Message);
        Assert.Equal("boom", ex2.Message);
    }
}
