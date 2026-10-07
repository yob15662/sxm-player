using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Net.Http;
using System.Text;

namespace SXMPlayer.Tests;

public class PlaylistServiceTests
{
    private static HttpResponseMessage Ok(string content)
        => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) };

    private static Streams PrimaryStream(string url)
        => new Streams
        {
            Urls = new List<Urls>
            {
                new()
                {
                    Name = "primary",
                    Url = url,
                    IsPrimary = true,
                    ValidUntil = "-",
                    EncryptionKeyId = "-"
                }
            }
        };

    [Fact]
    public async Task GetStreamPlaylistAsync_RewritesPlaylistAndBuildsTitles()
    {
        var playlistText = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-PROGRAM-DATE-TIME:2025-01-01T00:00:00.000Z",
            "#EXTINF:2.0,",
            "seg1.aac",
            "#EXTINF:2.0,",
            "seg2.aac",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"https://api.edge-gateway.siriusxm.com/playback/key/v1/00000000-0000-0000-0000-000000000000\"",
            "#EXT-X-PLAYLIST-TYPE:VOD",
            "#EXT-X-ENDLIST"
        });

        // Master playlist resolves to the media playlist URL. GetProxyPlaylistUrlAsync builds
        // the proxy URL as {scheme}://{host}{segments[0..^1]}{highestBandwidthVariant}, so the
        // master's single variant line "v3/index.m3u8" yields the media URL below.
        var masterUrl = "https://example.com/v1/token/sec-1/AAC_Data/channel/master.m3u8";
        var masterContent = string.Join("\n", new[]
        {
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=256000",
            "v3/index.m3u8"
        });
        var mediaUrl = "https://example.com/v1/token/sec-1/AAC_Data/channel/v3/index.m3u8";

        var tuner = new Mock<IStreamTuner>();
        tuner.Setup(t => t.TuneSourceAsync("channel", It.IsAny<int>()))
            .ReturnsAsync(PrimaryStream(masterUrl));

        var http = new Mock<IStreamHttpClient>();
        http.Setup(h => h.GetAsync(masterUrl)).ReturnsAsync(() => Ok(masterContent));
        http.Setup(h => h.GetAsync(mediaUrl)).ReturnsAsync(() => Ok(playlistText));

        var nowPlaying = new Mock<INowPlayingProvider>();
        nowPlaying.Setup(n => n.GetNowPlaying("channel", It.IsAny<DateTimeOffset?>(), It.IsAny<bool>()))
            .ReturnsAsync(("Artist 1", "Track 1", (string?)"id-1"));
        nowPlaying.Setup(n => n.GetNowPlaying())
            .Returns(new NowPlayingData("channel", "Fallback Artist", "Fallback Song", null));

        var currentChannel = Mock.Of<ICurrentChannelService>();

        var service = new PlaylistService(
            new NullLogger<PlaylistService>(),
            currentChannel,
            tuner.Object,
            http.Object,
            nowPlaying.Object,
            new PlayerState());

        var output = await service.GetStreamPlaylistAsync(
            channelId: "channel",
            currentId: SiriusXMPlayer.CURRENT_ID,
            alias: "channel",
            useCache: false);

        Assert.NotNull(output);
        Assert.Contains("#EXTINF:2,Artist 1 - Track 1", output);
        Assert.Contains("#EXTINF:2,Fallback Artist - Fallback Song", output);
        Assert.Contains("/stream/channel/v3/seg1.aac", output);
        Assert.Contains("/stream/channel/v3/seg2.aac", output);
        Assert.Contains("#EXT-X-KEY:METHOD=AES-128,URI=\"/key/00000000-0000-0000-0000-000000000000\"", output);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", output);
        Assert.DoesNotContain("EXT-X-ENDLIST", output);
        Assert.Equal(2.0, service.AverageSegmentDuration);
    }

    [Fact]
    public async Task GetProxyPlaylistUrlAsync_CachesStreamMetadata()
    {
        var sourceUrl = "https://example.com/v1/token/sec-1/AAC_Data/channel/master.m3u8";

        var tuner = new Mock<IStreamTuner>();
        tuner.Setup(t => t.TuneSourceAsync("channel", It.IsAny<int>()))
            .ReturnsAsync(PrimaryStream(sourceUrl));

        var http = new Mock<IStreamHttpClient>();
        http.Setup(h => h.GetAsync(It.IsAny<string>())).ReturnsAsync(() => Ok("bandwidth.m3u8"));

        var service = new PlaylistService(
            new NullLogger<PlaylistService>(),
            Mock.Of<ICurrentChannelService>(),
            tuner.Object,
            http.Object,
            Mock.Of<INowPlayingProvider>(),
            new PlayerState());

        var final = await service.GetProxyPlaylistUrlAsync("channel", useSecondary: false);

        Assert.Equal("https://example.com/v1/token/sec-1/AAC_Data/channel/bandwidth.m3u8", final);
        Assert.True(service.TryGetStream("channel", out var stream));
        Assert.NotNull(stream);
        Assert.Equal("https://example.com/v1/token/sec-1/AAC_Data/channel/", stream!.path);
    }
}
