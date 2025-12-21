using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
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
            // Construct an IcecastStreamer with minimal dependencies for testing
            var session = new APISession("http://localhost", _loggerFactoryMock.Object, Directory.GetCurrentDirectory(), "user", "pass");
            var player = new SiriusXMPlayer(_configurationMock.Object, _loggerMock.Object, _loggerFactoryMock.Object, _webHostEnvironmentMock.Object);
            var playerMock = new Mock<SiriusXMPlayer>(_configurationMock.Object, _loggerMock.Object, _loggerFactoryMock.Object, _webHostEnvironmentMock.Object);
            playerMock.Setup(p => p.GetNowPlaying()).Returns(new NowPlayingData("c", "Artist", "Title", null));
            var icecast = new IcecastStreamer(_loggerMock.Object, playerMock.Object);

            var testData = 1000400;
            //generate a random byte array of size 60000
            byte[] musicData = new byte[testData];
            new Random().NextBytes(musicData);
            var stream = new MemoryStream(musicData);
            var icyStream = await icecast.CreateICYStream(stream, ICY_SIZE);

            using var reader = new BinaryReader(icyStream);

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
                    // No metadata at EOF (shouldn't normally happen after a full chunk)
                    throw new InvalidDataException("invalid ICY stream: expected metadata length byte");
                }
                int metadataSize = metaLenByte * 16;

                // Read metadata payload if present
                string parsedTitle = string.Empty;
                if (metadataSize > 0)
                {
                    byte[] metadata = reader.ReadBytes(metadataSize);
                    // If the stream ended unexpectedly, stop
                    if (metadata.Length < metadataSize)
                    {
                        throw new InvalidDataException("invalid ICY stream: expected metadata length byte");
                    }
                    try
                    {
                        string metadataString = Encoding.UTF8.GetString(metadata);
                        var match = Regex.Match(metadataString, "StreamTitle='([^;]*)';");
                        if (match.Success)
                        {
                            parsedTitle = match.Groups[1].Value;
                        }
                    }
                    catch
                    {
                        throw;
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

            // Ensure we saw at least one metadata block with the expected title
            Assert.True(sawFirstTitle);

            // Compare reconstructed audio with original
            var reconstructedBytes = reconstructed.ToArray();
            Assert.Equal(musicData.Length, reconstructedBytes.Length);
            Assert.True(musicData.AsSpan().SequenceEqual(reconstructedBytes));
        }

        [Fact]
        public void TestMetadataBlock()
        {
            var player = new SiriusXMPlayer(_configurationMock.Object, _loggerMock.Object, _loggerFactoryMock.Object, _webHostEnvironmentMock.Object);
            var playerMock = new Mock<SiriusXMPlayer>(_configurationMock.Object, _loggerMock.Object, _loggerFactoryMock.Object, _webHostEnvironmentMock.Object);
            playerMock.Setup(p => p.GetNowPlaying()).Returns(new NowPlayingData("c", "Artist", "Title", null));

            var icecast = new IcecastStreamer(_loggerMock.Object, playerMock.Object);
            var block = icecast.GetMetadataBlock();
            var str = Encoding.UTF8.GetString(block);
            Assert.Equal(2, block[0]);
            Assert.Equal("StreamTitle='Artist - Title';", str.Substring(1, str.Length - 4));
        }
    }
}
