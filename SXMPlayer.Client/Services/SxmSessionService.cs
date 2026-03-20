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
        var inactivityAge = inactivityStart.HasValue ? DateTimeOffset.Now - inactivityStart.Value : (TimeSpan?)null;
        logger.LogDebug("LoginIfNecessary called - reason={Reason} inactivityStart={InactivityStart} inactivityAgeSeconds={InactivityAgeSeconds}",
            reason,
            inactivityStart,
            inactivityAge?.TotalSeconds);

        if (inactivityStart.HasValue && inactivityAge > TimeSpan.FromHours(2))
        {
            logger.LogInformation($"Re-logging in due to inactivity - source:{reason}");
            await session.ReLogin();
            InitializeActivityTimer();
            inactivityStart = null;
            logger.LogDebug("Re-login completed after inactivity - reason={Reason}", reason);
            return;
        }

        var doLogin = await session.LoginIfNecessary();
        logger.LogDebug("Session login check completed - reason={Reason} loginStateChanged={LoginStateChanged}", reason, doLogin);
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
            logger.LogDebug("Status checks already running; skipping StartStatusChecks call.");
            return;
        }

        logger.LogDebug("Starting status checks loop with 50s interval.");
        statusTimer = Task.Factory.StartNew(
            async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(50));
                while (!tokenSource.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(tokenSource.Token);
                    logger.LogDebug("Running status check request.");
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
