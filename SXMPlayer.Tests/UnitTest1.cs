using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SXMPlayer;
using System.Text.Json;

namespace SXMPlayer.Tests
{
    public record AuthData
    {
        public string handle { get; init; }
        public string password { get; init; }    
    }

    public class UnitTest1: IDisposable
    {
        private IConfigurationRoot configuration;
        private IConfigurationSection sxmConfiguration;
        private string tempFolder;
        private APISession session;
        private readonly ILoggerFactory loggerFactory;

        public UnitTest1()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(),"..","..", "..","..","SXMPlayer.Proxy"))
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Development.json", optional: true);
            configuration = builder.Build();
            sxmConfiguration = configuration.GetSection("SXM");
            //create logger factory for testing
            loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
            });
            var username = configuration.GetSection("SXM")["username"] ?? throw new InvalidProgramException("username is missing");
            var password = configuration.GetSection("SXM")["password"] ?? throw new InvalidProgramException("password is missing");
            //create temp folder
            tempFolder = Path.Combine(Path.GetTempPath(), "SXMPlayer");
            session = new APISession("https://api.edge-gateway.siriusxm.com", loggerFactory, tempFolder, username, password);
        }

        [Fact]
        public async Task Test_Authenticate()
        {
            await session.ClearAccessToken();
            var client = new Client(session.GetHttpClient());
            client.SetSession(session);
            // text : {"devicePlatform":"web-desktop","deviceAttributes":
            // {"browser":{"browserVersion":"121.0.0.0","browser":"Edge",
            // "userAgent":"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
            // "sdk":"web","app":"web","sdkVersion":"121.0.0.0","appVersion":"121.0.0.0"}}}
            var devAttributes = new DeviceAttributes2
            {
                Browser = new Browser2
                {
                    BrowserVersion = "121.0.0.0",
                    Browser = "Edge",
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
                    Sdk = "web",
                    App = "web",
                    SdkVersion = "121.0.0.0",
                    AppVersion = "121.0.0.0"
                },

            };
            var device = await client.DevicesAsync(new () { DevicePlatform = "web-desktop",  DeviceAttributes = devAttributes });
            Assert.NotNull(device);
            session.SetDevice(new DeviceInfo(device.DeviceId, device.Grant));
            var anon = await client.AnonymousAsync("");
            Assert.NotNull(anon);
            session.SetAnonymousAccessToken(anon.AccessToken, DateTimeOffset.Parse(anon.AccessTokenExpiresAt));
            var authData = new Body7 { Handle = sxmConfiguration["Username"], Password = sxmConfiguration["Password"] };
            var response = await client.PasswordAsync(authData);
            session.SetIdentityGrant(response.Grant);
            Assert.NotNull(response);            
        }

        [Fact]
        public async Task Test_ListChannels()
        {
            await session.ClearAccessToken();
            var client = new Client(session.GetHttpClient());
            client.SetSession(session);
            // text : {"devicePlatform":"web-desktop","deviceAttributes":
            // {"browser":{"browserVersion":"121.0.0.0","browser":"Edge",
            // "userAgent":"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
            // "sdk":"web","app":"web","sdkVersion":"121.0.0.0","appVersion":"121.0.0.0"}}}
            var devAttributes = new DeviceAttributes2
            {
                Browser = new Browser2
                {
                    BrowserVersion = "121.0.0.0",
                    Browser = "Edge",
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
                    Sdk = "web",
                    App = "web",
                    SdkVersion = "121.0.0.0",
                    AppVersion = "121.0.0.0"
                },

            };
            var device = await client.DevicesAsync(new () { DevicePlatform = "web-desktop", DeviceAttributes = devAttributes });
            Assert.NotNull(device);
            session.SetDevice(new DeviceInfo(device.DeviceId, device.Grant));
            var anon = await client.AnonymousAsync("");
            Assert.NotNull(anon);
            session.SetAnonymousAccessToken(anon.AccessToken, DateTimeOffset.Parse(anon.AccessTokenExpiresAt));
            var authData = new Body7 { Handle = sxmConfiguration["Username"], Password = sxmConfiguration["Password"] };
            var response = await client.PasswordAsync(authData);
            session.SetIdentityGrant(response.Grant);
            Assert.NotNull(response);
            //https://api.edge-gateway.siriusxm.com/relationship/v1/container/all-channels?
            //containerId=3JoBfOCIwo6FmTpzM1S2H7
            //&useCuratedContext=false
            //&entityType=curated-grouping
            //&entityId=403ab6a5-d3c9-4c2a-a722-a94a6a5fd056
            //&offset=0&size=30&setStyle=small_list&key=Y2U4MWE2OTMtNDdjNy00YWI4LTg4ZDUtOWU0NjU3ZDYwNzRm
            var authenticated = await client.AuthenticatedAsync(""); 
            Assert.NotNull(authenticated);
            session.SetIdentityToken(authenticated.AsSXMToken());
            var container = await client.AllChannelsAsync("3JoBfOCIwo6FmTpzM1S2H7", "false", "curated-grouping",
                "403ab6a5-d3c9-4c2a-a722-a94a6a5fd056", "0", "1000", "small_list", session.GetKey());
            var channels = container.Container.Sets.First().Items.ToList();
            Assert.NotNull(container);
            //var favorites = await client.li
            //var myLibrary = await client.MainViewAsync(session.GetKey());
            //Assert.NotNull(myLibrary);
        }

        [Fact]
        public async Task Test_ChannelRead()
        {
            await session.ClearAccessToken();
            var client = new Client(session.GetHttpClient());
            client.ReadResponseAsString = true;
            client.SetSession(session);
            // text : {"devicePlatform":"web-desktop","deviceAttributes":
            // {"browser":{"browserVersion":"121.0.0.0","browser":"Edge",
            // "userAgent":"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
            // "sdk":"web","app":"web","sdkVersion":"121.0.0.0","appVersion":"121.0.0.0"}}}
            var devAttributes = new DeviceAttributes2
            {
                Browser = new Browser2
                {
                    BrowserVersion = "121.0.0.0",
                    Browser = "Edge",
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
                    Sdk = "web",
                    App = "web",
                    SdkVersion = "121.0.0.0",
                    AppVersion = "121.0.0.0"
                },

            };
            var device = await client.DevicesAsync(new () { DevicePlatform = "web-desktop", DeviceAttributes = devAttributes });
            Assert.NotNull(device);
            session.SetDevice(new DeviceInfo(device.DeviceId, device.Grant));
            var anon = await client.AnonymousAsync("");
            Assert.NotNull(anon);
            session.SetAnonymousAccessToken(anon.AccessToken, DateTimeOffset.Parse(anon.AccessTokenExpiresAt));
            var authData = new Body7 { Handle = sxmConfiguration["Username"], Password = sxmConfiguration["Password"] };
            var response = await client.PasswordAsync(authData);
            session.SetIdentityGrant(response.Grant);
            Assert.NotNull(response);
            //https://api.edge-gateway.siriusxm.com/relationship/v1/container/all-channels?
            //containerId=3JoBfOCIwo6FmTpzM1S2H7
            //&useCuratedContext=false
            //&entityType=curated-grouping
            //&entityId=403ab6a5-d3c9-4c2a-a722-a94a6a5fd056
            //&offset=0&size=30&setStyle=small_list&key=Y2U4MWE2OTMtNDdjNy00YWI4LTg4ZDUtOWU0NjU3ZDYwNzRm
            var authenticated = await client.AuthenticatedAsync("");
            Assert.NotNull(authenticated);
            var tokenExpiry = DateTimeOffset.Parse(authenticated.AccessTokenExpiresAt!);
            var duration = tokenExpiry - DateTimeOffset.Now;
            var refreshExpiry = DateTimeOffset.Parse(authenticated.RefreshTokenExpiresAt!);
            var refreshDuration = refreshExpiry - DateTimeOffset.Now;
            var tokenDuration = tokenExpiry - DateTimeOffset.Now;
            session.SetIdentityToken(authenticated.AsSXMToken());
            var container = await client.AllChannelsAsync("3JoBfOCIwo6FmTpzM1S2H7", "false", "curated-grouping",
                "403ab6a5-d3c9-4c2a-a722-a94a6a5fd056", "0", "1000", "small_list", session.GetKey());
            Assert.NotNull(container);
            var channels = container.Container.Sets.First().Items.ToList();
            var channel1 = channels[0];
            var tuneSource = await client.TuneSourceAsync(new Body3 { 
                Id = channel1.Entity.Id,
                HlsVersion = "V3", 
                ManifestVariant = "WEB", Type = "channel-linear" });
            Assert.NotNull(tuneSource);
        }


        [Fact]
        public async Task Test_DecryptionKey()
        {
            await session.ClearAccessToken();
            var client = new Client(session.GetHttpClient());
            client.SetSession(session);
            // text : {"devicePlatform":"web-desktop","deviceAttributes":
            // {"browser":{"browserVersion":"121.0.0.0","browser":"Edge",
            // "userAgent":"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
            // "sdk":"web","app":"web","sdkVersion":"121.0.0.0","appVersion":"121.0.0.0"}}}
            var devAttributes = new DeviceAttributes2
            {
                Browser = new Browser2
                {
                    BrowserVersion = "121.0.0.0",
                    Browser = "Edge",
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
                    Sdk = "web",
                    App = "web",
                    SdkVersion = "121.0.0.0",
                    AppVersion = "121.0.0.0"
                },

            };
            var device = await client.DevicesAsync(new () { DevicePlatform = "web-desktop", DeviceAttributes = devAttributes });
            Assert.NotNull(device);
            session.SetDevice(new DeviceInfo(device.DeviceId, device.Grant));
            var anon = await client.AnonymousAsync("");
            Assert.NotNull(anon);
            session.SetAnonymousAccessToken(anon.AccessToken, DateTimeOffset.Parse(anon.AccessTokenExpiresAt));
            var authData = new Body7 { Handle = sxmConfiguration["Username"], Password = sxmConfiguration["Password"] };
            var response = await client.PasswordAsync(authData);
            session.SetIdentityGrant(response.Grant);
            Assert.NotNull(response);
            //https://api.edge-gateway.siriusxm.com/relationship/v1/container/all-channels?
            //containerId=3JoBfOCIwo6FmTpzM1S2H7
            //&useCuratedContext=false
            //&entityType=curated-grouping
            //&entityId=403ab6a5-d3c9-4c2a-a722-a94a6a5fd056
            //&offset=0&size=30&setStyle=small_list&key=Y2U4MWE2OTMtNDdjNy00YWI4LTg4ZDUtOWU0NjU3ZDYwNzRm
            var authenticated = await client.AuthenticatedAsync("");
            Assert.NotNull(authenticated);
            session.SetIdentityToken(authenticated.AsSXMToken());
            var decryptionKey = await client.V1Async("00000-0000-0000-0000-0000");
            Assert.NotNull(decryptionKey);
        }

        public void Dispose()
        {
            // try deleting temp folder
            try
            {
                Directory.Delete(tempFolder, true);
            }
            catch (Exception)
            {
                //ignore
            }
        }
    }
}