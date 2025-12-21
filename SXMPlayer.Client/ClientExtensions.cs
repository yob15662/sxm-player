using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SXMPlayer;

public partial class Client
{
    private APISession? _session;
    private int requestIndex = 0;

    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
    {
        settings.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    }

    partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, string url)
    {

    }
    partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, System.Text.StringBuilder urlBuilder)
    {
        //   x-sxm-clock : [0,0]
        request.Headers.Add("x-sxm-clock", $"[0,{requestIndex++}]");

        //x-sxm-platform : browser
        request.Headers.Add("x-sxm-platform", "browser");

        //x-sxm-tenant : sxm
        request.Headers.Add("x-sxm-tenant", "sxm");
        if (_session.AccessToken != null)
        {
            request.Headers.Add("Authorization", $"Bearer {_session.AccessToken.AccessToken}");
        }
        else
        if (_session.Tokens?.IdentityGrant != null)
        {
            request.Headers.Add("Authorization", $"Bearer {_session.Tokens.IdentityGrant}");
        }
        else if (_session.Tokens?.AnonAccessToken != null)
        {
            request.Headers.Add("Authorization", $"Bearer {_session.Tokens.AnonAccessToken}");
        }
        else if (_session.Device != null)
        {
            request.Headers.Add("Authorization", $"Bearer {_session.Device.DeviceGrant}");
        }
    }
    partial void ProcessResponse(System.Net.Http.HttpClient client, System.Net.Http.HttpResponseMessage response)
    {

    }

    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <summary>
    /// ondemand
    /// </summary>
    /// <returns>OK</returns>
    /// <exception cref="ApiException">A server side error occurred.</exception>
    public virtual async System.Threading.Tasks.Task<LibraryData> GetLibraryAsync(string key, System.Threading.CancellationToken cancellationToken)
    {
        if (key == null)
            throw new System.ArgumentNullException("key");

        var client_ = _httpClient;
        var disposeClient_ = false;
        try
        {
            using (var request_ = new System.Net.Http.HttpRequestMessage())
            {
                request_.Method = new System.Net.Http.HttpMethod("GET");
                request_.Headers.Accept.Add(System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

                var urlBuilder_ = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(BaseUrl)) urlBuilder_.Append(BaseUrl);
                urlBuilder_.Append("ondemand");
                urlBuilder_.Append('/');
                urlBuilder_.Append("v1");
                urlBuilder_.Append('/');
                urlBuilder_.Append("library");
                urlBuilder_.Append('/');
                urlBuilder_.Append("all");
                urlBuilder_.Append('?');
                urlBuilder_.Append(System.Uri.EscapeDataString("key") + "=").Append(System.Uri.EscapeDataString(ConvertToString(key, System.Globalization.CultureInfo.InvariantCulture))).Append("&");
                urlBuilder_.Length--;

                PrepareRequest(client_, request_, urlBuilder_);

                var url_ = urlBuilder_.ToString();
                request_.RequestUri = new System.Uri(url_, System.UriKind.RelativeOrAbsolute);

                PrepareRequest(client_, request_, url_);

                var response_ = await client_.SendAsync(request_, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var disposeResponse_ = true;
                try
                {
                    var headers_ = System.Linq.Enumerable.ToDictionary(response_.Headers, h_ => h_.Key, h_ => h_.Value);
                    if (response_.Content != null && response_.Content.Headers != null)
                    {
                        foreach (var item_ in response_.Content.Headers)
                            headers_[item_.Key] = item_.Value;
                    }

                    ProcessResponse(client_, response_);

                    var status_ = (int)response_.StatusCode;
                    if (status_ == 200)
                    {
                        var objectResponse_ = await ReadObjectResponseAsync<LibraryData>(response_, headers_, cancellationToken).ConfigureAwait(false);
                        if (objectResponse_.Object == null)
                        {
                            throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                        }
                        return objectResponse_.Object;
                    }
                    else
                    {
                        var responseData_ = response_.Content == null ? null : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new ApiException("Unexpected error", status_, responseData_, headers_, null);
                    }
                }
                finally
                {
                    if (disposeResponse_)
                        response_.Dispose();
                }
            }
        }
        finally
        {
            if (disposeClient_)
                client_.Dispose();
        }
    }

    public void SetSession(APISession session)
    {
        _session = session;
    }

    //https://api.edge-gateway.siriusxm.com/relationship/v1/container/all-channels?containerId=3JoBfOCIwo6FmTpzM1S2H7&useCuratedContext=false&entityType=curated-grouping&entityId=403ab6a5-d3c9-4c2a-a722-a94a6a5fd056&offset=0&size=30&setStyle=small_list&key=Y2U4MWE2OTMtNDdjNy00YWI4LTg4ZDUtOWU0NjU3ZDYwNzRm
}

public partial class LibraryData
{

    [System.Text.Json.Serialization.JsonPropertyName("profileId")]

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? ProfileId { get; set; } = default!;

    [System.Text.Json.Serialization.JsonPropertyName("allDataMap")]

    public System.Collections.Generic.IDictionary<string, SXMLibraryElement> AllDataMap
    {
        get { return _allDataMap ?? (_allDataMap = new System.Collections.Generic.Dictionary<string, SXMLibraryElement>()); }
        set { _allDataMap = value; }
    }
    private System.Collections.Generic.IDictionary<string, SXMLibraryElement>? _allDataMap;

    [System.Text.Json.Serialization.JsonExtensionData]
    public System.Collections.Generic.IDictionary<string, object> AdditionalProperties
    {
        get { return _additionalProperties ?? (_additionalProperties = new System.Collections.Generic.Dictionary<string, object>()); }
        set { _additionalProperties = value; }
    }

    private System.Collections.Generic.IDictionary<string, object>? _additionalProperties;

}

public partial class SXMLibraryElement
{
    [System.Text.Json.Serialization.JsonPropertyName("entityId")]
    public string EntityId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("entityType")]
    public string EntityType { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("createdTime")]
    public long CreatedTime { get; set; }
}
