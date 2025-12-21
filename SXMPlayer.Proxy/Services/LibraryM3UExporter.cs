
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text;
using System.Threading.Channels;

namespace SXMPlayer.Proxy.Services
{
    public class LibraryM3UExporter : IHostedService
    {
        private readonly ILogger<LibraryM3UExporter> logger;
        private readonly IServer server;
        private readonly SiriusXMPlayer sxmPlayer;
        private readonly IHostApplicationLifetime lifetime;
        private string? outputFolder;
        private readonly bool disable_mDNS;

        public LibraryM3UExporter(
            ILogger<LibraryM3UExporter> logger,
            IConfiguration configuration,
            IServer server,
            SiriusXMPlayer siriusXMPlayer, 
            IHostApplicationLifetime lifetime)
        {
            outputFolder = configuration["LibraryM3UExportFolder"];
            disable_mDNS = configuration.GetValue<bool>("Disable_mDNS", false);
            this.logger = logger;
            this.server = server;
            this.sxmPlayer = siriusXMPlayer;
            this.lifetime = lifetime;            
        }

        private string? BuildServerUrl(ICollection<string> addresses)
        {
            if (addresses == null || addresses.Count == 0)
            {
                return null;
            }
            // prefer http protocol
            var preferredAddress = addresses.FirstOrDefault(a => a.StartsWith("http://"));
            if (preferredAddress is null)
            {
                preferredAddress = addresses.FirstOrDefault();
            }
            if (preferredAddress is null)
            {
                return null;
            }
            var uri = new Uri(preferredAddress);
            var port = uri.Port;
            var protocol = uri.Scheme;
            //get current host name
            return $"{protocol}://{GetHostName()}:{port}";
        }
        
        public string GetHostName()
        {
            var hostname = Environment.GetEnvironmentVariable("HOST_HOSTNAME") ?? Dns.GetHostName();
            var mDNS = disable_mDNS ? "" : ".local";
            return $"{hostname}{mDNS}";
        }    

        //LibraryM3UExportFolder
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                return;
            }
            logger.LogInformation($"M3U Exporter is enabled. Output folder: {outputFolder} - Hostname: {GetHostName()}");
            //check if folder exists
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            lifetime.ApplicationStarted.Register(async () =>
            {
                var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
                if (addresses != null)
                {
                    await DumpFavorites(addresses);
                }
            });

        }

        private async Task DumpFavorites(ICollection<string> addresses)
        {
            var serverUrl = BuildServerUrl(addresses);
            var favoriteIds = await sxmPlayer.GetFavoritesAsync();
            if (serverUrl is null || favoriteIds is null)
            {
                return;
            }
            var allChannels = await sxmPlayer.GetChannelsAsync();
            foreach (var favorite in favoriteIds)
            {
                var channel = allChannels.FirstOrDefault(c => c.Entity.Id == favorite)?.Entity;
                if (channel is not null && !string.IsNullOrWhiteSpace(channel.Filename))
                {
                    var playlist = Tools.GeneratePlaylistContent(favorite, channel.ChannelName!, serverUrl);
                    var playlistPath = Path.Combine(outputFolder!, $"{channel.Filename}.m3u");
                    await File.WriteAllTextAsync(playlistPath, playlist);
                }
            }
            // generate current.m3u
            var cPlaylist = Tools.GeneratePlaylistContent("current", "Current", serverUrl);
            var cPlaylistPath = Path.Combine(outputFolder!, $"current.m3u");
            await File.WriteAllTextAsync(cPlaylistPath, cPlaylist);

            // generate SiriusXM.m3u
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            foreach (var favorite in favoriteIds)
            {
                var channel = allChannels.FirstOrDefault(c => c.Entity.Id == favorite);
                var channelId = channel?.Entity.Filename;
                if (!string.IsNullOrWhiteSpace(channelId))
                {
                    sb.AppendLine($"#EXTINF:-1,{channel?.Entity?.Texts?.Title?.Default}");
                    var playlistLink = Tools.GeneratePlaylistLink(favorite, serverUrl);
                    sb.AppendLine($"{playlistLink}");
                }
            }
            //add current
            sb.AppendLine("#EXTINF:-1,Current Channel");            
            sb.AppendLine($"{Tools.GeneratePlaylistLink("current", serverUrl)}");

            var combinedPlaylist = Path.Combine(outputFolder!, $"SiriusXM.m3u");
            await File.WriteAllTextAsync(combinedPlaylist, sb.ToString());
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
