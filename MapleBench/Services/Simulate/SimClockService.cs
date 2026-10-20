using MapleBench.Services.Simulate;

namespace MapleBench.Services.Simulate;

/// <summary>
/// The game's clock, on the server.
/// </summary>
/// <remarks>
/// <para>
/// This class exists only to hold a loop. The loop itself, the accumulator and
/// the fixed step live in <see cref="MapSimSessionStore.Tick"/>, because that is
/// the thing holding the sessions, and a clock that could not reach them would
/// be a clock that does nothing.
/// </para>
/// <para>
/// WHY ONE LOOP OVER ALL SESSIONS, AND NOT ONE TIMER PER SESSION
/// ------------------------------------------------------------
/// A per-session timer is the obvious shape here and it is the wrong one.
/// Three reasons, in order of how much they would have cost:
///
///   * A session is created by an HTTP request. Its lifetime is not the
///     caller's to schedule, so a per-session timer means a timer per body,
///     started and disposed around request handling -- and an exception path
///     that misses the dispose leaves a timer firing at a session that no
///     longer exists. That is the leak this change was supposed to remove.
///   * Timers do not overlap by themselves, but they serialize on a thread
///     pool, so N bodies become N wakeups 240 times a second each. One loop is
///     one wakeup.
///   * Sweeping has to happen whether or not anything is stepping. A loop that
///     owns the heartbeat is the only place that guarantee comes from; a
///     per-session timer sweeps nothing, because it only exists while the
///     session does.
///
/// OVERLAPPING TICKS
/// -----------------
/// Ticks cannot overlap, and not because a lock prevents it. There is one loop
/// and one loop calls <c>Tick</c>, so two ticks are sequential by construction
/// -- the same reason two ticks of the client's Update() cannot overlap. What
/// CAN overlap is a tick against a request, and that is handled where it
/// happens: physics takes the editor's <c>Gate</c>, which every caller of
/// <c>body.Step</c> already took before this change, so a clock tick and a POST
/// frame serialize on that lock rather than racing. The clock does not hold it
/// between passes, so a long map load cannot stall it.
///
/// The one deliberate cost is that a clock tick and a POST frame can interleave
/// in the frame COUNTER -- the POST is not gated against the loop. That is
/// harmless here: the counter is monotonic, callers read positions rather than
/// indices, and the manual lease means it does not even come up, because a
/// caller mid-sequence has the clock stood down.
/// </remarks>
public sealed class SimClockService : BackgroundService
{
    private readonly MapSimSessionStore _sessions;
    private readonly ILogger<SimClockService> _log;

    public SimClockService(MapSimSessionStore sessions, ILogger<SimClockService> log)
    {
        _sessions = sessions;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(MapSimSessionStore.LoopInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _sessions.Tick(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
                return;
            }
            catch (Exception ex)
            {
                // The try is INSIDE the loop, and moving it there is the whole fix.
                // It used to wrap the loop, so the first exception to escape ended
                // this method for good — BackgroundService never calls ExecuteAsync
                // a second time — every session froze for the life of the process,
                // and the only symptom was this one line. The comment that stood
                // here claimed "One bad pass is logged and the loop carries on",
                // which was the exact opposite of what the code did.
                //
                // A per-session step failure is handled inside Tick, which stands
                // that session down after a few passes and keeps going. Reaching
                // THIS catch is not "a session misbehaved": it is Sweep, or
                // enumerating the session collection, neither of which belongs to
                // any one session — and the clock must outlive both.
                _log.LogError(ex, "the simulator clock pass failed; the clock keeps running");
            }
        }
    }
}