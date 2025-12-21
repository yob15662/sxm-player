using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SXMPlayer;

/// <summary>
/// Handles HLS AES-128 decryption helpers and key retrieval.
/// </summary>
public class HlsEncryptionService
{
    private readonly APISession _session;
    private readonly ILogger _logger;
    private readonly Dictionary<string, byte[]> _keyCache = new();

    // Define the regex pattern for a GUID
    internal static readonly Regex GuidPattern = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    public HlsEncryptionService(APISession session, ILogger logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<byte[]> GetDecryptionKey(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) throw new ArgumentNullException(nameof(guid));
        if (_keyCache.TryGetValue(guid, out var key))
        {
            return key;
        }
        var decryptionKey = await _session.apiClient.V1Async(guid);
        var hls_aes_key = Convert.FromBase64String(decryptionKey.Key);
        _keyCache[guid] = hls_aes_key;
        return hls_aes_key;
    }

    public static byte[]? BuildIVFromSequence(long? seq)
    {
        if (!seq.HasValue) return null;
        var iv = new byte[16];
        var v = (ulong)seq.Value;
        for (int i = 0; i < 8; i++)
        {
            iv[15 - i] = (byte)(v & 0xFF);
            v >>= 8;
        }
        return iv;
    }

    public static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length % 2 == 1) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public static byte[] DecryptAes128Cbc(byte[] cipher, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }
}
