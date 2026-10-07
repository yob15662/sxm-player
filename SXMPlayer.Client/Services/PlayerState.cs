namespace SXMPlayer;

/// <summary>
/// Process-wide, thread-safe player state shared across the fetch and refresh paths.
/// Owns the stale-refresh escalation state machine that decides when stream URL
/// selection should fall back from the primary to the secondary URL. Registered as a
/// DI singleton so a single instance governs the escalation decision process-wide.
/// </summary>
public class PlayerState
{
    /// <summary>
    /// Number of consecutive (debounced) playlist refreshes caused by stale/404 segments
    /// before the stream URL selection falls back to the secondary URL. Reset once a
    /// channel change gives us a known-good fresh start.
    /// </summary>
    public const int SecondaryFallbackRefreshThreshold = 2;

    private readonly object _gate = new();
    private int _consecutiveStaleRefreshes;
    private bool _useSecondaryStreamUrl;

    /// <summary>
    /// When true, the next source playlist fetch prefers the secondary stream URL.
    /// Set after repeated stale refreshes; cleared on a genuine channel change.
    /// Thread-safe read for the lock-free fetch path.
    /// </summary>
    public bool UseSecondaryStreamUrl
    {
        get
        {
            lock (_gate)
            {
                return _useSecondaryStreamUrl;
            }
        }
    }

    /// <summary>
    /// Current count of consecutive stale refreshes. Thread-safe read used for logging.
    /// </summary>
    public int ConsecutiveStaleRefreshes
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveStaleRefreshes;
            }
        }
    }

    /// <summary>
    /// Atomically records a stale playlist refresh. Increments the consecutive-refresh
    /// counter and, if not already on the secondary URL and the counter has reached
    /// <see cref="SecondaryFallbackRefreshThreshold"/>, flips to the secondary URL and
    /// resets the counter so the secondary gets a clean run before further escalation.
    /// </summary>
    /// <returns>
    /// A tuple of <c>Escalated</c> (<c>true</c> when this call caused the fallback to the
    /// secondary URL) and <c>ConsecutiveStaleRefreshes</c> (the counter value observed after
    /// the increment but before any escalation reset, so callers can log the pre-reset count).
    /// </returns>
    public (bool Escalated, int ConsecutiveStaleRefreshes) RegisterStaleRefresh()
    {
        lock (_gate)
        {
            _consecutiveStaleRefreshes++;
            var observedCount = _consecutiveStaleRefreshes;

            if (!_useSecondaryStreamUrl && _consecutiveStaleRefreshes >= SecondaryFallbackRefreshThreshold)
            {
                _useSecondaryStreamUrl = true;
                _consecutiveStaleRefreshes = 0;
                return (true, observedCount);
            }

            return (false, observedCount);
        }
    }

    /// <summary>
    /// Resets the stale-refresh escalation state back to the primary stream URL. Clears
    /// the consecutive-refresh counter unconditionally and the secondary-URL fallback flag.
    /// </summary>
    /// <returns><c>true</c> when the secondary-URL flag had been set (so callers can log
    /// the reset only when it actually changed).</returns>
    public bool ResetStreamUrlFallback()
    {
        lock (_gate)
        {
            _consecutiveStaleRefreshes = 0;
            if (_useSecondaryStreamUrl)
            {
                _useSecondaryStreamUrl = false;
                return true;
            }

            return false;
        }
    }
}
