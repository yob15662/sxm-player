using System.Net.Http;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Raw HTTP send for stream/playlist/segment URLs. Applies the resilience pipeline and
/// translates non-success responses into the player's exception contract:
/// <see cref="ApiException"/> on 403, <see cref="SegmentNotFoundException"/> on 404, and
/// <see cref="System.InvalidOperationException"/> on any other non-OK status.
/// </summary>
public interface IStreamHttpClient
{
    Task<HttpResponseMessage?> GetAsync(string url);
}
