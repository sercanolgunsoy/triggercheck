using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

public class MonitorStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CheckOutcome Ok = CheckOutcome.Up(42, 200);
    private static readonly CheckOutcome Fail = CheckOutcome.Down("Connection refused", 7);

    private static MonitorEntity NewMonitor(MonitorStatus status = MonitorStatus.Unknown, int threshold = 2) =>
        new() { Status = status, FailureThreshold = threshold, IntervalSeconds = 15 };

    [Fact]
    public void Unknown_to_up_on_first_success_without_alert_transition()
    {
        var m = NewMonitor();
        Assert.Equal(StatusTransition.BecameUp, MonitorStateMachine.Apply(m, Ok, T0));
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(T0, m.StatusChangedAtUtc);
        Assert.Equal(T0, m.LastCheckedAtUtc);
        Assert.Equal(T0.AddSeconds(15), m.NextCheckAtUtc);
        Assert.Equal(42, m.LastResponseTimeMs);
        Assert.Null(m.LastError);
    }

    [Fact]
    public void Up_goes_down_only_when_threshold_reached()
    {
        var m = NewMonitor(MonitorStatus.Up, threshold: 3);

        Assert.Equal(StatusTransition.None, MonitorStateMachine.Apply(m, Fail, T0));
        Assert.Equal(StatusTransition.None, MonitorStateMachine.Apply(m, Fail, T0.AddSeconds(15)));
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(2, m.ConsecutiveFailures);
        Assert.Equal("Connection refused", m.LastError);

        Assert.Equal(StatusTransition.WentDown, MonitorStateMachine.Apply(m, Fail, T0.AddSeconds(30)));
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal(T0.AddSeconds(30), m.StatusChangedAtUtc);

        // further failures do not re-trigger the transition
        Assert.Equal(StatusTransition.None, MonitorStateMachine.Apply(m, Fail, T0.AddSeconds(45)));
        Assert.Equal(4, m.ConsecutiveFailures);
        Assert.Equal(T0.AddSeconds(30), m.StatusChangedAtUtc);
    }

    [Fact]
    public void Failure_streak_is_reset_by_a_success()
    {
        var m = NewMonitor(MonitorStatus.Up, threshold: 2);
        MonitorStateMachine.Apply(m, Fail, T0);
        Assert.Equal(StatusTransition.None, MonitorStateMachine.Apply(m, Ok, T0.AddSeconds(15)));
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal(StatusTransition.None, MonitorStateMachine.Apply(m, Fail, T0.AddSeconds(30)));
        Assert.Equal(MonitorStatus.Up, m.Status);
    }

    [Fact]
    public void Down_recovers_on_first_success()
    {
        var m = NewMonitor(MonitorStatus.Down);
        m.ConsecutiveFailures = 5;
        Assert.Equal(StatusTransition.Recovered, MonitorStateMachine.Apply(m, Ok, T0));
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal(T0, m.StatusChangedAtUtc);
    }

    [Fact]
    public void Unknown_stays_unknown_below_threshold_then_goes_down()
    {
        var m = NewMonitor(threshold: 2);
        Assert.Equal(StatusTransition.None, MonitorStateMachine.Apply(m, Fail, T0));
        Assert.Equal(MonitorStatus.Unknown, m.Status);
        Assert.Null(m.StatusChangedAtUtc);
        Assert.Equal(StatusTransition.WentDown, MonitorStateMachine.Apply(m, Fail, T0.AddSeconds(15)));
        Assert.Equal(MonitorStatus.Down, m.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Threshold_of_one_or_less_goes_down_immediately(int threshold)
    {
        var m = NewMonitor(MonitorStatus.Up, threshold);
        Assert.Equal(StatusTransition.WentDown, MonitorStateMachine.Apply(m, Fail, T0));
    }

    [Fact]
    public void RecordProbe_does_not_touch_status_or_schedule()
    {
        var m = NewMonitor(MonitorStatus.Paused);
        m.NextCheckAtUtc = null;
        MonitorStateMachine.RecordProbe(m, Fail, T0);
        Assert.Equal(MonitorStatus.Paused, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.NextCheckAtUtc);
        Assert.Equal(T0, m.LastCheckedAtUtc);
        Assert.Equal("Connection refused", m.LastError);
    }

    // ---- ENG-3: schedule from the previously scheduled time ----

    [Fact]
    public void Next_check_is_counted_from_the_scheduled_time_not_the_late_run()
    {
        var m = NewMonitor(MonitorStatus.Up);
        m.NextCheckAtUtc = T0;
        MonitorStateMachine.Apply(m, Ok, T0.AddMilliseconds(900)); // ran 0.9s late (tick + dispatch latency)
        Assert.Equal(T0.AddSeconds(15), m.NextCheckAtUtc);
        MonitorStateMachine.Apply(m, Ok, T0.AddSeconds(15.9));
        Assert.Equal(T0.AddSeconds(30), m.NextCheckAtUtc);
    }

    [Fact]
    public void Next_check_realigns_to_now_when_schedule_fell_behind()
    {
        var m = NewMonitor(MonitorStatus.Up);
        m.NextCheckAtUtc = T0;
        MonitorStateMachine.Apply(m, Ok, T0.AddMinutes(10)); // e.g. resumed / overloaded: no burst of catch-up checks
        Assert.Equal(T0.AddMinutes(10).AddSeconds(15), m.NextCheckAtUtc);
    }

    [Theory]
    [InlineData(null)]  // first run
    [InlineData(10)]    // early run ("check now" before the scheduled time) does not push the schedule further out
    public void Next_check_counts_from_the_run_without_or_before_a_schedule(int? scheduledInSeconds)
    {
        var scheduled = scheduledInSeconds is int s ? T0.AddSeconds(s) : (DateTime?)null;
        Assert.Equal(T0.AddSeconds(15), MonitorStateMachine.NextCheckAfter(scheduled, 15, T0));
    }

    [Fact]
    public void Next_check_exactly_one_interval_late_moves_forward()
    {
        Assert.Equal(T0.AddSeconds(30), MonitorStateMachine.NextCheckAfter(T0, 15, T0.AddSeconds(15)));
    }

    // ---- ENG-1: monitor paused while Down ----

    [Fact]
    public void First_success_after_pause_while_down_is_a_recovery()
    {
        var m = NewMonitor(MonitorStatus.Unknown);
        m.DownWhenPaused = true;
        Assert.Equal(StatusTransition.Recovered, MonitorStateMachine.Apply(m, Ok, T0));
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.False(m.DownWhenPaused);
    }

    [Fact]
    public void Going_down_after_pause_while_down_clears_the_flag()
    {
        var m = NewMonitor(MonitorStatus.Unknown, threshold: 1);
        m.DownWhenPaused = true;
        Assert.Equal(StatusTransition.WentDown, MonitorStateMachine.Apply(m, Fail, T0));
        Assert.False(m.DownWhenPaused);
    }
}
