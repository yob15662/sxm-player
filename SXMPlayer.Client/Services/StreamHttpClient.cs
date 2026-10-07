using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Raw HTTP send with the Polly resilience pipeline and the 403/404/non-OK translation.
/// Moved verbatim from <c>SiriusXMPlayer.GetHttpResponseMessage</c>/<c>ConfigureRequest</c>/
/// <c>getQueryParameters()</c>/the <c>httpRetryPipeline</c> build.
/// </summary>
public class StreamHttpClient : IStreamHttpClient
{
    private readonly APISession session;
    private readonly CancellationTokenSource tokenSource;
    private readonly ILogger<StreamHttpClient> logger;
    private readonly ResiliencePipeline<HttpResponseMessage> httpRetryPipeline;

    public StreamHttpClient(APISession session, CancellationTokenSource tokenSource, ILogger<StreamHttpClient> logger)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Configure Polly resilience pipeline for HTTP retries
        httpRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response => !response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Forbidden && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .Handle<HttpRequestException>(),
                OnRetry = args =>
                {
                    logger.LogWarning($"HTTP request retry attempt {args.AttemptNumber} after {args.RetryDelay.TotalSeconds:F1}s delay. Outcome: {args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString()}");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public async Task<HttpResponseMessage?> GetAsync(string url)
    {
        var parameters = getQueryParameters();
        var builder = new UriBuilder(url);
        builder.Query = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

        return await httpRetryPipeline.ExecuteAsync(async ct =>
        {
            var request = new HttpRequestMessage() { RequestUri = builder.Uri, Method = HttpMethod.Get };
            ConfigureRequest(request);

            var response = await session.GetHttpClient().SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var responseData_ = response.Content == null ? null : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new ApiException("Unexpected error", (int)response.StatusCode, responseData_, null, null);
            }
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new SegmentNotFoundException(url);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Received status code {response.StatusCode} for url \'{url}\'");
            }

            return response;
        }, tokenSource.Token);
    }

    private static void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(Windows NT 10.0; Win64; x64)"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AppleWebKit", "537.36"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(KHTML, like Gecko)"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Chrome", "101.0.4911.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Safari", "537.36"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Edg", "101.0.1193.0"));
    }

    private Dictionary<string, string> getQueryParameters() => new Dictionary<string, string> { };
}
