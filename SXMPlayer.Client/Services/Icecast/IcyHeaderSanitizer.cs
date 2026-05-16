using System;
using System.Text;

namespace SXMPlayer;

/// <summary>
/// Sanitizes values for ICY/Icecast HTTP headers to ensure they contain only ASCII characters.
/// HTTP headers must contain only ASCII characters per RFC 7230.
/// </summary>
public static class IcyHeaderSanitizer
{
    /// <summary>
    /// Sanitizes a header value to contain only ASCII characters.
    /// Non-ASCII characters are replaced with their closest ASCII equivalent where possible,
    /// or removed if no reasonable equivalent exists.
    /// </summary>
    /// <param name="value">The header value to sanitize</param>
    /// <returns>A sanitized string containing only ASCII characters, or an empty string if the input is null or empty</returns>
    public static string SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (ch <= 127)
            {
                // ASCII character - keep it
                result.Append(ch);
            }
            else
            {
                // Non-ASCII character - try to find a replacement or skip it
                var replacement = GetAsciiReplacement(ch);
                if (!string.IsNullOrEmpty(replacement))
                {
                    result.Append(replacement);
                }
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Gets the ASCII replacement for a non-ASCII character.
    /// </summary>
    private static string GetAsciiReplacement(char ch)
    {
        return ch switch
        {
            // Common Latin-1 supplement and accented characters
            'À' or 'Á' or 'Â' or 'Ã' or 'Ä' or 'Å' => "A",
            'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' => "a",
            'Ç' or 'ç' => "c",
            'Ð' or 'ð' => "d",
            'È' or 'É' or 'Ê' or 'Ë' => "E",
            'è' or 'é' or 'ê' or 'ë' => "e",
            'Ì' or 'Í' or 'Î' or 'Ï' => "I",
            'ì' or 'í' or 'î' or 'ï' => "i",
            'Ñ' => "N",
            'ñ' => "n",
            'Ò' or 'Ó' or 'Ô' or 'Õ' or 'Ö' or 'Ø' => "O",
            'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' => "o",
            'Ù' or 'Ú' or 'Û' or 'Ü' => "U",
            'ù' or 'ú' or 'û' or 'ü' => "u",
            'ý' or 'Ý' or 'ÿ' => "y",
            'Æ' => "AE",
            'æ' => "ae",
            'Œ' => "OE",
            'œ' => "oe",
            'ß' => "ss",
            'þ' => "th",
            'Þ' => "TH",
            _ => string.Empty
        };
    }
}
