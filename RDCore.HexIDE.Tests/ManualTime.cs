namespace RDCore.HexIDE.Tests;

/// <summary>
/// A clock that moves only when a test moves it: its timers fire, in order, on the thread that calls <see cref="Advance"/>,
/// so what a timer does is done when <see cref="Advance"/> returns.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<Timer> timers = [];
    private DateTimeOffset now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private TaskCompletionSource armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Whether any timer is due to fire.</summary>
    public bool IsArmed
    {
        get
        {
            lock (gate)
            {
                return timers.Any(timer => timer.Due is not null);
            }
        }
    }

    /// <summary>Completes the next time a timer is set to fire.</summary>
    public Task WhenArmed()
    {
        lock (gate)
        {
            return armed.Task;
        }
    }

    /// <summary>Moves the clock on by <paramref name="by"/>, firing every timer that falls due on the way, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate)
        {
            target = now + by;
        }

        while (true)
        {
            Timer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.Due <= target).MinBy(timer => timer.Due);
                if (next is null)
                {
                    now = target;
                    return;
                }

                now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? now + next.Period : null;
            }
            next.Fire();
        }
    }

    private void Armed()
    {
        TaskCompletionSource fired;
        lock (gate)
        {
            fired = armed;
            armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fired.TrySetResult();
    }

    private sealed class Timer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : time.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            if (Due is not null)
            {
                time.Armed();
            }
            return true;
        }

        public void Dispose()
        {
            lock (time.gate)
            {
                Due = null;
                time.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
