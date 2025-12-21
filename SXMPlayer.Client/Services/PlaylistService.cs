using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Handles fetching and rewriting HLS playlists, time mapping, and channel selection.
/// </summary>
public class PlaylistService
{
    private readonly APISession _session;
    private readonly ILogger<SiriusXMPlayer> _logger;

    private readonly Dictionary<string, DateTimeOffset?> _streamTimeMap = new();

    private static readonly Regex ExtRegex = new("#EXT-X-PROGRAM-DATE-TIME:(.*)", RegexOptions.Compiled);
    private static readonly Regex ExtInfRegex = new(@"#EXTINF:(?<duration>[^,]+)(,(?<title>.*))?", RegexOptions.Compiled);

    public PlaylistService(APISession session, ILogger<SiriusXMPlayer> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyDictionary<string, DateTimeOffset?> StreamTimeMap => _streamTimeMap;

    public static double? ReadExtInfDuration(string line)
    {
        var match = ExtInfRegex.Match(line);
        if (match.Success)
        {
            return double.Parse(match.Groups["duration"].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static TimeZoneInfo estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public void ExtractTimeMap(IEnumerable<string> m3u8Content)
    {
        lock (_streamTimeMap)
        {
            _streamTimeMap.Clear();
            DateTimeOffset? dateTime = null;
            foreach (var line in m3u8Content)
            {
                var match = ExtRegex.Match(line);
                if (match.Success)
                {
                    var badUTC = match.Groups[1].Value;
                    string cleanDateString = badUTC.Replace("+00:00", "").TrimEnd('Z');
                    DateTime estTime = DateTime.Parse(cleanDateString);
                    dateTime = TimeZoneInfo.ConvertTime(estTime, estZone).ToUniversalTime();
                }
                if (!line.StartsWith("#"))
                {
                    var segmentName = line.Split('/').Last();
                    if (!string.IsNullOrWhiteSpace(segmentName))
                    {
                        _streamTimeMap.TryAdd(segmentName, dateTime);
                        dateTime = null;
                    }
                    else
                    {
                        _logger.LogWarning($"invalid line - no segment found: '{line}'");
                    }
                }
            }
            if (_streamTimeMap.Count == 0)
            {
                _logger.LogWarning($"Cannot parse m3u8: '{string.Join(',', m3u8Content)}'");
            }
        }
    }

    public static string[] SplitLines(string? res)
    {
        if (string.IsNullOrEmpty(res)) return Array.Empty<string>();
        return res.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    // Redirect the EXT-X-KEY URI to our proxy endpoint
    public static string RedirectKeyIfFound(string line)
    {
        if (line.StartsWith("#EXT-X-KEY:METHOD=AES-128,URI="))
        {
            var matches = HlsEncryptionService.GuidPattern.Matches(line);
            if (matches.Count > 0)
            {
                string lastGuid = matches[matches.Count - 1].Value;
                return $"#EXT-X-KEY:METHOD=AES-128,URI=\"/key/{lastGuid}\"";
            }
            else
            {
                throw new InvalidCastException($"No GUID found: '{line}'");
            }
        }
        return line;
    }
}
