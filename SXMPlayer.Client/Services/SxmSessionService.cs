using Microsoft.Extensions.Logging;

namespace SXMPlayer;

public sealed class SxmSessionService : IDisposable
{
    private readonly APISession session;
    private readonly ILogger<SxmSessionService> logger;
    private readonly CancellationTokenSource tokenSource;
    private readonly Action? activityTimeoutHandler;

    private DateTimeOffset? inactivityStart;
    private Task? activityTimerTask;
    private Task<Task>? statusTimer;

    public SxmSessionService(APISession session, ILogger<SxmSessionService> logger, CancellationTokenSource tokenSource, Action? activityTimeoutHandler)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        this.activityTimeoutHandler = activityTimeoutHandler;
    }

    public async Task LoginIfNecessary(string reason)
    {
        if (inactivityStart.HasValue && DateTimeOffset.Now - inactivityStart > TimeSpan.FromHours(2))
        {
            logger.LogInformation($"Re-logging in due to inactivity - source:{reason}");
            await session.ReLogin();
            InitializeActivityTimer();
            inactivityStart = null;
            return;
        }

        var doLogin = await session.LoginIfNecessary();
        if (doLogin)
        {
            InitializeActivityTimer();
        }
    }

    public void MarkInactivityStart(DateTimeOffset timestamp)
    {
        inactivityStart = timestamp;
    }

    public void InitializeActivityTimer()
    {
        if (activityTimeoutHandler != null)
        {
            activityTimerTask ??= Task.Factory.StartNew(activityTimeoutHandler, tokenSource.Token)
                .ContinueWith(_ => logger.LogError(_.Exception, "Error with timeout handler"), TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void StartStatusChecks()
    {
        if (statusTimer is not null)
        {
            return;
        }

        statusTimer = Task.Factory.StartNew(
            async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(50));
                while (!tokenSource.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(tokenSource.Token);
                    var statusResponse = await session.apiClient.StatusAsync();
                    logger.LogDebug($"Status response - state={statusResponse.State}");
                    if (statusResponse?.State?.ToLower() != "active")
                    {
                        logger.LogCritical($"Status is not active - state={statusResponse.State}");
                        tokenSource.Cancel();
                    }
                }
            });
    }

    public void Dispose()
    {
        activityTimerTask?.Dispose();
    }
}
