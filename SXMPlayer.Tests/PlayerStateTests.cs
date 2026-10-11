using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer.Tests;

public class PlayerStateTests
{
    [Fact]
    public void DefaultState_IsPrimary()
    {
        var state = new PlayerState();

        Assert.False(state.UseSecondaryStreamUrl);
        Assert.Equal(0, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void FirstStaleRefresh_DoesNotEscalate()
    {
        var state = new PlayerState();

        var (escalated, observedCount, bothStreamsStale) = state.RegisterStaleRefresh();

        Assert.False(escalated);
        Assert.False(bothStreamsStale);
        Assert.Equal(1, observedCount);
        Assert.False(state.UseSecondaryStreamUrl);
        Assert.Equal(1, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void SecondStaleRefresh_EscalatesAtThreshold_AndResetsCounter()
    {
        var state = new PlayerState();

        state.RegisterStaleRefresh();
        var (escalated, observedCount, bothStreamsStale) = state.RegisterStaleRefresh();

        Assert.True(escalated);
        Assert.False(bothStreamsStale);
        // The observed count mirrors the pre-reset value the old log line reported.
        Assert.Equal(PlayerState.SecondaryFallbackRefreshThreshold, observedCount);
        Assert.True(state.UseSecondaryStreamUrl);
        Assert.Equal(0, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void AfterEscalation_DoesNotEscalateAgain_ButCounterStillIncrements()
    {
        var state = new PlayerState();

        state.RegisterStaleRefresh();
        state.RegisterStaleRefresh(); // escalates here

        var (escalated, observedCount, bothStreamsStale) = state.RegisterStaleRefresh();

        Assert.False(escalated);
        Assert.False(bothStreamsStale);
        Assert.Equal(1, observedCount);
        Assert.True(state.UseSecondaryStreamUrl);
        Assert.Equal(1, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void SecondaryAlsoStale_SignalsBothStreamsStale_AtThreshold_AndResetsCounter()
    {
        var state = new PlayerState();

        state.RegisterStaleRefresh();
        state.RegisterStaleRefresh(); // escalates to the secondary URL, counter resets to 0

        // Now the secondary URL itself goes stale. The first refresh does not yet signal;
        // the one that reaches the threshold reports BothStreamsStale and resets the counter.
        var first = state.RegisterStaleRefresh();
        var second = state.RegisterStaleRefresh();

        Assert.False(first.Escalated);
        Assert.False(first.BothStreamsStale);
        Assert.Equal(1, first.ConsecutiveStaleRefreshes);

        Assert.False(second.Escalated);
        Assert.True(second.BothStreamsStale);
        Assert.Equal(PlayerState.SecondaryFallbackRefreshThreshold, second.ConsecutiveStaleRefreshes);

        // Still on the secondary URL; the counter resets so the signal re-fires on a cadence.
        Assert.True(state.UseSecondaryStreamUrl);
        Assert.Equal(0, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void ResetAfterEscalation_ReturnsToPrimary_AndReportsChange()
    {
        var state = new PlayerState();

        state.RegisterStaleRefresh();
        state.RegisterStaleRefresh(); // escalates

        var reportedChange = state.ResetStreamUrlFallback();

        Assert.True(reportedChange);
        Assert.False(state.UseSecondaryStreamUrl);
        Assert.Equal(0, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void ResetWhenPrimary_ReportsNoChange_ButClearsCounter()
    {
        var state = new PlayerState();

        // Counter advanced but flag never set.
        state.RegisterStaleRefresh();

        var reportedChange = state.ResetStreamUrlFallback();

        Assert.False(reportedChange);
        Assert.False(state.UseSecondaryStreamUrl);
        Assert.Equal(0, state.ConsecutiveStaleRefreshes);
    }

    [Fact]
    public void ThresholdConstant_IsTwo()
    {
        Assert.Equal(2, PlayerState.SecondaryFallbackRefreshThreshold);
    }

    [Fact]
    public async Task RegisterStaleRefresh_UnderContention_EscalatesExactlyOnce()
    {
        var state = new PlayerState();
        const int taskCount = 64;
        var escalationCount = 0;
        var startGate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
        {
            startGate.Wait();
            var (escalated, _, _) = state.RegisterStaleRefresh();
            if (escalated)
            {
                Interlocked.Increment(ref escalationCount);
            }
        })).ToArray();

        startGate.Set();
        await Task.WhenAll(tasks);

        // The read-modify-write is atomic, so exactly one caller observes the escalation
        // and the flag ends up set.
        Assert.Equal(1, Volatile.Read(ref escalationCount));
        Assert.True(state.UseSecondaryStreamUrl);
    }

    [Fact]
    public async Task Escalation_IsVisibleAcrossThreads()
    {
        var state = new PlayerState();

        await Task.Run(() =>
        {
            state.RegisterStaleRefresh();
            state.RegisterStaleRefresh(); // escalates on this thread
        });

        // After the join point a different thread observes the write.
        var observed = await Task.Run(() => state.UseSecondaryStreamUrl);

        Assert.True(observed);
    }
}
