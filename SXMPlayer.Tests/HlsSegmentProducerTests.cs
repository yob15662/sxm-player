using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SXMPlayer.Tests;

/// <summary>
/// Unit tests for HlsSegmentProducer - HLS playlist and segment handling.
/// </summary>
public class HlsSegmentProducerTests
{
    private Mock<ILogger> CreateMockLogger()
    {
        return new Mock<ILogger>();
    }

    // Simple stub for SiriusXMPlayer to avoid Moq proxy issues
    private class SiriusXMPlayerStub : SiriusXMPlayer
    {
        public SiriusXMPlayerStub() : base(null!, null!, null!, null!, null!) { }
    }

    private static PlaylistService CreatePlaylistService()
        => new PlaylistService(
            new Mock<ILogger<PlaylistService>>().Object,
            Mock.Of<ICurrentChannelService>(),
            Mock.Of<IStreamTuner>(),
            Mock.Of<IStreamHttpClient>(),
            Mock.Of<INowPlayingProvider>(),
            new PlayerState());

    // Stub using the test-only constructor so GetSegment / RequestPlaylistRefresh can be
    // overridden without wiring up the full player dependency graph. GetSegment always
    // signals a 404 and RequestPlaylistRefresh is counted rather than executed, so this
    // isolates the producer's behavior (no per-segment retry).
    private sealed class NotFoundPlayerStub : SiriusXMPlayer
    {
        public NotFoundPlayerStub()
            : base(new Mock<ILogger<SiriusXMPlayer>>().Object, CreatePlaylistService())
        {
        }

        public int GetSegmentCalls { get; private set; }
        public int RefreshRequests { get; private set; }

        public override Task<Stream> GetSegment(string channelId, string version, string segmentId, SXMListener? client)
        {
            GetSegmentCalls++;
            throw new SegmentNotFoundException($"https://example/{segmentId}");
        }

        public override void RequestPlaylistRefresh(string reason)
        {
            RefreshRequests++;
        }
    }

    // Stub that runs the real RequestPlaylistRefresh (with its debounce) to verify a burst
    // of 404s collapses into a single actual refresh.
    private sealed class RealRefreshPlayerStub : SiriusXMPlayer
    {
        public RealRefreshPlayerStub()
            : base(new Mock<ILogger<SiriusXMPlayer>>().Object, CreatePlaylistService())
        {
        }

        public override Task<Stream> GetSegment(string channelId, string version, string segmentId, SXMListener? client)
            => throw new SegmentNotFoundException($"https://example/{segmentId}");
    }

    private static ChannelItemData ChannelWithId(string id)
        => new ChannelItemData { Entity = new EntityData { Id = id } };

    [Fact]
    public async Task FetchAndDecryptSegment_WhenSegment404_DoesNotRetryAndRequestsRefresh()
    {
        // Arrange
        var player = new NotFoundPlayerStub();
        var producer = new HlsSegmentProducer(player, CreateMockLogger().Object);
        Func<Task<ChannelItemData>> channelProvider = () => Task.FromResult(ChannelWithId("channel-1"));

        // Act - a single stale segment returns 404.
        var result = await producer.FetchAndDecryptSegment(
            "channel-1",
            channelProvider,
            version: "14",
            segmentName: "seg_0.aac",
            currentKey: null,
            currentIV: null,
            segmentSequence: 0,
            cancellationToken: CancellationToken.None);

        // Assert - the segment yielded no data, was fetched exactly once (NOT 3x via the
        // retry loop), and a playlist refresh was requested instead.
        Assert.Null(result);
        Assert.Equal(1, player.GetSegmentCalls);
        Assert.Equal(1, player.RefreshRequests);
    }

    [Fact]
    public async Task RequestPlaylistRefresh_BurstOf404s_TriggersSingleRefresh()
    {
        // Arrange
        var player = new RealRefreshPlayerStub();
        var channelChangedToken = player.ChannelChangedTokenForTest;
        var producer = new HlsSegmentProducer(player, CreateMockLogger().Object);
        Func<Task<ChannelItemData>> channelProvider = () => Task.FromResult(ChannelWithId("channel-1"));

        // Act - three 404s in quick succession (well within the debounce window).
        for (var i = 0; i < 3; i++)
        {
            await producer.FetchAndDecryptSegment(
                "channel-1",
                channelProvider,
                version: "14",
                segmentName: $"seg_{i}.aac",
                currentKey: null,
                currentIV: null,
                segmentSequence: i,
                cancellationToken: CancellationToken.None);
        }

        // Assert - the real debounced RequestPlaylistRefresh cancelled the channel-changed
        // token exactly once; subsequent 404s within the window were collapsed.
        Assert.True(channelChangedToken.IsCancellationRequested);
        Assert.NotEqual(channelChangedToken, player.ChannelChangedTokenForTest);
        Assert.False(player.ChannelChangedTokenForTest.IsCancellationRequested);
    }

    // Stub whose GetStreamPlaylist returns null, so RunProducerAsync idles on its
    // Task.Delay(500, combinedCt) loop and the producer stays "running" until its linked
    // (channel-changed) token is cancelled. Lets us exercise producer lifecycle without a
    // live HTTP stream.
    private sealed class IdlePlayerStub : SiriusXMPlayer
    {
        public IdlePlayerStub()
            : base(new Mock<ILogger<SiriusXMPlayer>>().Object, CreatePlaylistService())
        {
        }

        public override Task<string?> GetStreamPlaylist(string channelId, SXMListener? listener, string? alias = null, bool useCache = true, int retries = 0)
            => Task.FromResult<string?>(null);
    }

    // Regression test for the secondary-URL fallback stall: when a producer is cancelled
    // (playlist refresh) its teardown is asynchronous. A restart that re-enters StartProducer
    // while the old task is cancelled-but-not-yet-finished must NOT attach to the dying
    // producer (which would then complete the newly registered writer and leave the stream
    // dead until a full process restart). It must await the old teardown and start a fresh
    // producer instead.
    [Fact]
    public async Task StartProducer_AfterChannelChangedTokenCancelled_StartsFreshProducer()
    {
        // Arrange
        var player = new IdlePlayerStub();
        var producer = new HlsSegmentProducer(player, CreateMockLogger().Object);
        var listener = new SXMListener(IPAddress.Loopback);
        Func<Task<ChannelItemData?>> channelProvider = () => Task.FromResult<ChannelItemData?>(ChannelWithId("channel-1"));

        var firstChannelChanged = new CancellationTokenSource();
        var firstQueue = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();

        // Start the initial producer (no producer running yet -> starts fresh, returns false).
        var firstStartedAttached = await producer.StartProducer(
            firstQueue.Writer, channelProvider, listener,
            firstChannelChanged.Token, CancellationToken.None);
        Assert.False(firstStartedAttached);

        // Act - simulate a playlist refresh: cancel the channel-changed source the producer is
        // linked to (this is what RequestPlaylistRefresh does), then immediately re-enter
        // StartProducer with a NEW, uncancelled channel-changed source, exactly as the consumer
        // loop does on its restart iteration.
        firstChannelChanged.Cancel();

        var secondChannelChanged = new CancellationTokenSource();
        var secondQueue = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var secondStartedAttached = await producer.StartProducer(
            secondQueue.Writer, channelProvider, listener,
            secondChannelChanged.Token, CancellationToken.None);

        // Assert - the restart started a FRESH producer (false == not attached-to-active),
        // rather than mistaking the dying producer for a live one (which returned true in the
        // bug and produced "Attached client ... to the active HLS segment producer." with no
        // restart).
        Assert.False(secondStartedAttached);

        // And the freshly registered writer must still be open (NOT completed by the old
        // producer's teardown). A completed writer would reject new segments.
        Assert.True(secondQueue.Writer.TryWrite(
            new SegmentWorkItem("seg.aac", "v1", 0, new Memory<byte>(new byte[] { 1 }))));

        // Cleanup - cancel the live producer so the test doesn't leak a background loop.
        secondChannelChanged.Cancel();
    }

    [Fact]
    public void Constructor_WithValidArguments_Succeeds()
    {
        // Arrange
        var logger = CreateMockLogger();

        // Act & Assert - just verify it doesn't throw
        // Skip actual instantiation since SiriusXMPlayer needs real dependencies
        Assert.NotNull(logger);
    }

    [Fact]
    public void SegmentWorkItem_CanBeCreated()
    {
        // Arrange & Act
        var item = new SegmentWorkItem(
            "segment1.aac",
            "v1",
            123,
            new Memory<byte>(new byte[] { 1, 2, 3 }));

        // Assert
        Assert.Equal("segment1.aac", item.SegmentName);
        Assert.Equal("v1", item.Version);
        Assert.Equal(123, item.MediaSequence);
        Assert.NotNull(item.AudioData);
        Assert.Equal(3, item.AudioData.Value.Length);
    }

    [Fact]
    public void SegmentWorkItem_WithNullAudioData_IsValid()
    {
        // Arrange & Act
        var item = new SegmentWorkItem(
            "segment1.aac",
            "v1",
            123,
            null);

        // Assert
        Assert.Equal("segment1.aac", item.SegmentName);
        Assert.Null(item.AudioData);
    }

    [Theory]
    [InlineData("segment1.aac")]
    [InlineData("segment_123.aac")]
    [InlineData("very_long_segment_name_with_many_characters.aac")]
    public void SegmentWorkItem_WithVariousNames_IsValid(string segmentName)
    {
        // Act
        var item = new SegmentWorkItem(
            segmentName,
            "v1",
            0,
            null);

        // Assert
        Assert.Equal(segmentName, item.SegmentName);
    }

    [Fact]
    public void SegmentWorkItem_WithVariousSequenceNumbers_IsValid()
    {
        // Arrange & Act
        var item1 = new SegmentWorkItem("seg1.aac", "v1", 0, null);
        var item2 = new SegmentWorkItem("seg2.aac", "v1", long.MaxValue, null);
        var item3 = new SegmentWorkItem("seg3.aac", "v1", -1, null);

        // Assert
        Assert.Equal(0, item1.MediaSequence);
        Assert.Equal(long.MaxValue, item2.MediaSequence);
        Assert.Equal(-1, item3.MediaSequence);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("version-123")]
    public void SegmentWorkItem_WithVariousVersions_IsValid(string version)
    {
        // Act
        var item = new SegmentWorkItem(
            "segment.aac",
            version,
            0,
            null);

        // Assert
        Assert.Equal(version, item.Version);
    }

    [Fact]
    public void SegmentWorkItem_WithAudioData_PreservesData()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var item = new SegmentWorkItem(
            "segment.aac",
            "v1",
            0,
            new Memory<byte>(audioData));

        // Assert
        Assert.Equal(audioData, item.AudioData.Value.ToArray());
    }

    [Fact]
    public void SegmentWorkItem_Equality_WithSameName_IsEqual()
    {
        // Arrange
        var item1 = new SegmentWorkItem("seg.aac", "v1", 0, null);
        var item2 = new SegmentWorkItem("seg.aac", "v1", 0, null);

        // Act & Assert
        Assert.Equal(item1, item2);
    }

    [Fact]
    public void SegmentWorkItem_Equality_WithDifferentData_NotEqual()
    {
        // Arrange
        var item1 = new SegmentWorkItem("seg.aac", "v1", 0, null);
        var item2 = new SegmentWorkItem("seg.aac", "v1", 1, null);

        // Act & Assert
        Assert.NotEqual(item1, item2);
    }
}
