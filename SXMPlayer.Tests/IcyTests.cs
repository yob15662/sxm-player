using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using SXMPlayer;

namespace SXMPlayer.Tests
{
    public class IcyTests
    {
        public const int ICY_SIZE = 16000;
        private readonly Mock<IConfiguration> _configurationMock;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<IWebHostEnvironment> _webHostEnvironmentMock;
        private readonly Mock<ILogger<SiriusXMPlayer>> _loggerMock;
        private readonly Mock<ILogger<APISession>> _apiSessionLoggerMock;

        public IcyTests()
        {
            _configurationMock = new Mock<IConfiguration>();
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _webHostEnvironmentMock = new Mock<IWebHostEnvironment>();
            _loggerMock = new Mock<ILogger<SiriusXMPlayer>>();
            _apiSessionLoggerMock = new Mock<ILogger<APISession>>();

            _webHostEnvironmentMock.Setup(w => w.ContentRootPath).Returns("");
            var sxmSectionMock = new Mock<IConfigurationSection>();
            sxmSectionMock.Setup(s => s["username"]).Returns("testuser");
            sxmSectionMock.Setup(s => s["password"]).Returns("testpass");
            sxmSectionMock.Setup(s => s["cacheFolder"]).Returns(Path.GetTempPath());

            var mqttSectionMock = new Mock<IConfigurationSection>();
            mqttSectionMock.Setup(s => s["Server"]).Returns("localhost");

            _configurationMock.Setup(c => c.GetSection("SXM")).Returns(sxmSectionMock.Object);
            _configurationMock.Setup(c => c.GetSection("MQTT")).Returns(mqttSectionMock.Object);

            _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>()))
                .Returns(new Mock<ILogger>().Object);
            _loggerFactoryMock.Setup(x => x.CreateLogger("SXMPlayer.SiriusXMPlayer"))
                .Returns(_loggerMock.Object);
            _loggerFactoryMock.Setup(x => x.CreateLogger("SXMPlayer.APISession"))
                .Returns(_apiSessionLoggerMock.Object);
        }

        [Fact]
        public async Task TestIcyMetadataParsing()
        {
            var playerMock = new Mock<SiriusXMPlayer>(_configurationMock.Object, _loggerMock.Object, _loggerFactoryMock.Object, _webHostEnvironmentMock.Object);
            playerMock.Setup(p => p.GetNowPlaying()).Returns(new NowPlayingData("c", "Artist", "Title", null));

            // Create actual instances for MetadataService dependencies (not used in this test anyway)
            var apiSession = new APISession("https://test.example.com", _loggerFactoryMock.Object, "", "user", "pass");
            var sxmSessionService = new SxmSessionService(apiSession, Mock.Of<ILogger<SxmSessionService>>(), new CancellationTokenSource(), null);
            var playlistService = new PlaylistService(Mock.Of<ILogger<PlaylistService>>());

            // Create a mock MetadataService
            var metadataServiceMock = new Mock<MetadataService>(
                Mock.Of<ILogger<MetadataService>>(),
                apiSession,
                sxmSessionService,
                playlistService,
                CancellationToken.None) { CallBase = false };
            metadataServiceMock.Setup(m => m.GetNowPlaying()).Returns(new NowPlayingData("c", "Artist", "Title", null));

            var metadataBuilder = new IcyMetadataBuilder();
            var streamWriter = new IcyStreamWriter(metadataBuilder, _loggerMock.Object, metadataServiceMock.Object);

            var testData = 1000400;
            byte[] musicData = new byte[testData];
            new Random().NextBytes(musicData);

            // Use IcyStreamWriter output via HttpContext response body
            var ctx = new DefaultHttpContext();
            var output = new MemoryStream();
            ctx.Response.Body = output;

            int bytesUntilMeta = ICY_SIZE;
            bytesUntilMeta = await streamWriter.WriteAsync(musicData.AsMemory(), ctx, injectMetadata: true, metadataInterval: ICY_SIZE, bytesUntilMeta, CancellationToken.None);

            output.Position = 0;
            using var reader = new BinaryReader(output);

            // Reconstruct original audio by stripping ICY metadata blocks while validating titles
            using var reconstructed = new MemoryStream();
            bool sawFirstTitle = false;

            while (true)
            {
                // Read up to ICY_SIZE bytes of audio
                byte[] audioChunk = reader.ReadBytes(ICY_SIZE);
                if (audioChunk.Length == 0)
                {
                    // End of stream
                    break;
                }
                reconstructed.Write(audioChunk, 0, audioChunk.Length);

                // If we didn't get a full chunk, this is the tail; no metadata follows
                if (audioChunk.Length < ICY_SIZE)
                {
                    break;
                }

                // Read metadata length byte (in 16-byte blocks)
                int metaLenByte;
                try
                {
                    metaLenByte = reader.ReadByte();
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException("invalid ICY stream: expected metadata length byte");
                }
                int metadataSize = metaLenByte * 16;

                // Read metadata payload if present
                string parsedTitle = string.Empty;
                if (metadataSize > 0)
                {
                    byte[] metadata = reader.ReadBytes(metadataSize);
                    if (metadata.Length < metadataSize)
                    {
                        throw new InvalidDataException("invalid ICY stream: expected metadata payload");
                    }

                    string metadataString = Encoding.UTF8.GetString(metadata);
                    var match = Regex.Match(metadataString, "StreamTitle='([^;]*)';");
                    if (match.Success)
                    {
                        parsedTitle = match.Groups[1].Value;
                    }
                }

                // Validate first metadata contains title, subsequent ones should be empty (since title didn't change)
                if (!sawFirstTitle)
                {
                    Assert.Equal("Artist - Title", parsedTitle);
                    sawFirstTitle = true;
                }
                else
                {
                    Assert.Equal(string.Empty, parsedTitle);
                }
            }

            Assert.True(sawFirstTitle);

            // Compare reconstructed audio with original
            var reconstructedBytes = reconstructed.ToArray();
            Assert.Equal(musicData.Length, reconstructedBytes.Length);
            Assert.True(musicData.AsSpan().SequenceEqual(reconstructedBytes));
        }

        [Fact]
        public void TestMetadataBlock()
        {
            // Create actual instances for MetadataService dependencies (not used in this test anyway)
            var apiSession = new APISession("https://test.example.com", _loggerFactoryMock.Object, "", "user", "pass");
            var sxmSessionService = new SxmSessionService(apiSession, Mock.Of<ILogger<SxmSessionService>>(), new CancellationTokenSource(), null);
            var playlistService = new PlaylistService(Mock.Of<ILogger<PlaylistService>>());

            // Create a mock MetadataService that returns now playing data
            var metadataServiceMock = new Mock<MetadataService>(
                Mock.Of<ILogger<MetadataService>>(),
                apiSession,
                sxmSessionService,
                playlistService,
                CancellationToken.None) { CallBase = false };
            metadataServiceMock.Setup(m => m.GetNowPlaying()).Returns(new NowPlayingData("c", "Artist", "Title", null));

            // Create a mock player - not used in this specific test but required by IcecastStreamer constructor
            var playerMock = new Mock<SiriusXMPlayer>(_configurationMock.Object, _loggerMock.Object, _loggerFactoryMock.Object, _webHostEnvironmentMock.Object);

            var icecast = new IcecastStreamer(_loggerMock.Object, metadataServiceMock.Object, playerMock.Object);
            var block = icecast.GetMetadataBlock();
            var str = Encoding.UTF8.GetString(block);
            Assert.Equal(2, block[0]);
            Assert.Equal("StreamTitle='Artist - Title';", str.Substring(1, str.Length - 4));
        }
    }
}
