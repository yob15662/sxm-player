using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http;
using System.Text;

namespace SXMPlayer.Tests;

public class PlaylistServiceTests
{
    [Fact]
    public async Task GetStreamPlaylistAsync_RewritesPlaylistAndBuildsTitles()
    {
        var service = new PlaylistService(new NullLogger<PlaylistService>());
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

        var proxyUrl = "https://example.com/v1/token/sec-1/AAC_Data/channel/v3/index.m3u8";

        var output = await service.GetStreamPlaylistAsync(
            channelId: "channel",
            currentId: SiriusXMPlayer.CURRENT_ID,
            alias: "channel",
            useCache: false,
            currentChannel: null,
            setCurrentChannel: _ => Task.CompletedTask,
            getProxyPlaylistUrl: _ => Task.FromResult(proxyUrl),
            getHttpResponse: _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playlistText, Encoding.UTF8)
            }),
            getNowPlaying: (_, _) => Task.FromResult<(string artist, string title, string? id)?>(("Artist 1", "Track 1", "id-1")),
            nowPlayingFallback: new NowPlayingData("channel", "Fallback Artist", "Fallback Song", null));

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
        var service = new PlaylistService(new NullLogger<PlaylistService>());
        var sourceUrl = "https://example.com/v1/token/sec-1/AAC_Data/channel/master.m3u8";

        var final = await service.GetProxyPlaylistUrlAsync(
            "channel",
            _ => Task.FromResult(new Streams
            {
                Urls = new List<Urls>
                {
                    new()
                    {
                        Name = "primary",
                        Url = sourceUrl,
                        IsPrimary = true,
                        ValidUntil = "-",
                        EncryptionKeyId = "-"
                    }
                }
            }),
            _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("bandwidth.m3u8")
            }));

        Assert.Equal("https://example.com/v1/token/sec-1/AAC_Data/channel/bandwidth.m3u8", final);
        Assert.True(service.TryGetStream("channel", out var stream));
        Assert.NotNull(stream);
        Assert.Equal("https://example.com/v1/token/sec-1/AAC_Data/channel/", stream!.path);
    }
}
