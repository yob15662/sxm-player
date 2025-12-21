using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SXMPlayer
{
    public class Tools
    {
        internal static async Task<T?> ReadJsonFile<T>(string fileName, ILogger logger) where T : class
        {
            try
            {
                if (File.Exists(fileName))
                    return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(fileName))!;
                return null;
            }
            catch (Exception)
            {
                logger.LogWarning($"Cannot read file {fileName}");
                File.Delete(fileName);
                return null;
            }
        }
        internal static async Task WriteObject<T>(T? obj, string fileName, ILogger logger) where T : class
        {
            try
            {
                if (obj is null)
                    File.Delete(fileName);
                else
                    await File.WriteAllTextAsync(fileName, JsonSerializer.Serialize(obj));
            }
            catch (Exception)
            {
                logger.LogWarning($"Cannot write file {fileName}");
            }
        }

        public static string GeneratePlaylistLink(string channelId, string serverUrl)
        {
            //strip the server url with last /
            if (serverUrl.EndsWith("/"))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 1);
            // Direct Icecast stream link
            return $"{serverUrl}/icecast/{channelId}";
        }

        public static string GeneratePlaylistContent(string channelId, string channelName, string serverUrl)
        {
            //strip the server url with last /
            if (serverUrl.EndsWith("/"))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 1);
            // Simple M3U pointing to the Icecast endpoint
            return "#EXTM3U\n" +
                $"#EXTINF:-1,SiriusXM - {channelName}\n" +
                $"{serverUrl}/icecast/{channelId}";
        }
    }
}
