using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Threading;

namespace SXMPlayer.Tests;

public class ProgressTimerManagerTests
{
    private static APISession CreateSession() =>
        new("https://example.com", NullLoggerFactory.Instance, Path.GetTempPath(), "user", "pass");

    private static MetadataService CreateMetadataService(NowPlayingData nowPlaying, DateTimeOffset audioTs, ChannelItemData channel)
    {
        var session = CreateSession();
        var cts = new CancellationTokenSource();
        var sxmSessionService = new SxmSessionService(session, NullLogger<SxmSessionService>.Instance, cts, null);
        var playlistService = new PlaylistService(NullLogger<PlaylistService>.Instance);
        var currentChannelFile = Path.Combine(Path.GetTempPath(), $"currentChannel.{Guid.NewGuid()}.json");
        return new TestMetadataService(
            NullLogger<MetadataService>.Instance,
            session,
            sxmSessionService,
            playlistService,
            currentChannelFile,
            cts.Token,
            nowPlaying,
            audioTs,
            channel);
    }

    [Fact]
    public async Task Start_RunsCallbackImmediately()
    {
        var callCount = 0;
        var channel = new ChannelItemData
        {
            Entity = new EntityData { Id = "channel-1", Type = "channel-linear" }
        };

        var session = CreateSession();
        var metadataService = CreateMetadataService(
            new NowPlayingData("channel-1", "artist", "song", "track-1"),
            DateTimeOffset.UtcNow,
            channel);

        using var manager = new ProgressTimerManager(
            new NullLogger<ProgressTimerManager>(),
            () => true,
            metadataService,
            session,
            CancellationToken.None,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new Response15 { FailedActions = new List<object>() });
            });

        manager.Start(isChannelChanged: false);

        var start = Stopwatch.StartNew();
        while (Volatile.Read(ref callCount) == 0 && start.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(10);
        }

        Assert.True(Volatile.Read(ref callCount) > 0);
    }

    [Fact]
    public async Task Stops_WhenNoActiveListeners()
    {
        var callCount = 0;
        var hasListeners = true;
        var channel = new ChannelItemData
        {
            Entity = new EntityData { Id = "channel-1", Type = "channel-linear" }
        };

        var session = CreateSession();
        var metadataService = CreateMetadataService(
            new NowPlayingData("channel-1", "artist", "song", "track-1"),
            DateTimeOffset.UtcNow,
            channel);

        using var manager = new ProgressTimerManager(
            new NullLogger<ProgressTimerManager>(),
            () => hasListeners,
            metadataService,
            session,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(40),
            (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new Response15 { FailedActions = new List<object>() });
            });

        manager.Start(isChannelChanged: false);

        var start = Stopwatch.StartNew();
        while (Volatile.Read(ref callCount) == 0 && start.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(10);
        }

        Assert.True(Volatile.Read(ref callCount) > 0);

        hasListeners = false;
        var beforeStop = Volatile.Read(ref callCount);
        await Task.Delay(200);

        Assert.Equal(beforeStop, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task Stops_WhenProgressContextIdsAreMissing()
    {
        var sendCount = 0;
        var hasListenerChecks = 0;
        var channel = new ChannelItemData
        {
            Entity = new EntityData { Id = "channel-1", Type = "channel-linear" }
        };

        var session = CreateSession();
        var metadataService = CreateMetadataService(
            new NowPlayingData("channel-1", "artist", "song", null),
            DateTimeOffset.UtcNow,
            channel);

        using var manager = new ProgressTimerManager(
            new NullLogger<ProgressTimerManager>(),
            () =>
            {
                Interlocked.Increment(ref hasListenerChecks);
                return true;
            },
            metadataService,
            session,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(40),
            (_, _) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(new Response15 { FailedActions = new List<object>() });
            });

        manager.Start(isChannelChanged: false);

        await Task.Delay(160);
        var checksAfterStop = Volatile.Read(ref hasListenerChecks);
        await Task.Delay(160);

        Assert.Equal(0, Volatile.Read(ref sendCount));
        Assert.Equal(checksAfterStop, Volatile.Read(ref hasListenerChecks));
    }

    private sealed class TestMetadataService : MetadataService
    {
        private readonly NowPlayingData _nowPlaying;
        private readonly DateTimeOffset _audioTs;
        private readonly ChannelItemData _currentChannel;

        public TestMetadataService(
            ILogger<MetadataService> logger,
            APISession session,
            SxmSessionService sxmSessionService,
            PlaylistService playlistService,
            string currentChannelFile,
            CancellationToken cancellationToken,
            NowPlayingData nowPlaying,
            DateTimeOffset audioTs,
            ChannelItemData currentChannel)
            : base(logger, session, sxmSessionService, playlistService, currentChannelFile, cancellationToken)
        {
            _nowPlaying = nowPlaying;
            _audioTs = audioTs;
            _currentChannel = currentChannel;
        }

        public override NowPlayingData? GetNowPlaying() => _nowPlaying;

        public override DateTimeOffset? GetAudioOriginalTimestamp() => _audioTs;

        public override ChannelItemData? GetCurrentChannel() => _currentChannel;
    }
}
