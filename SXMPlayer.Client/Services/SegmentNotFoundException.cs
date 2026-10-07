using System;
using System.Net;

namespace SXMPlayer;

/// <summary>
/// Thrown when an HLS segment fetch returns HTTP 404 NotFound.
/// Signals that the current playlist is stale and should be refreshed,
/// rather than retrying the same segment.
/// </summary>
public class SegmentNotFoundException : Exception
{
    public string Url { get; }

    public HttpStatusCode StatusCode { get; }

    public SegmentNotFoundException(string url)
        : this(url, null)
    {
    }

    public SegmentNotFoundException(string url, Exception? innerException)
        : base($"Received status code {HttpStatusCode.NotFound} for url '{url}'", innerException)
    {
        Url = url;
        StatusCode = HttpStatusCode.NotFound;
    }
}
