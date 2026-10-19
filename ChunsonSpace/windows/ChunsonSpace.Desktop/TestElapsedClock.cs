using System.Diagnostics;
using System.Windows.Threading;

namespace ChunsonSpace.Desktop;

internal sealed class TestElapsedClock(Action changed)
{
    private readonly Stopwatch watch = new();
    private DispatcherTimer? timer;
    public bool Started { get; private set; }
    public string Text => "本轮用时 " + Format(watch.Elapsed.TotalSeconds) + "（含排队）";
    public void Start()
    {
        Stop(); Started = true; watch.Restart();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += Tick; timer.Start(); changed();
    }
    private void Tick(object? sender, EventArgs e) => changed();
    public void Stop()
    {
        watch.Stop();
        if (timer is not null) { timer.Stop(); timer.Tick -= Tick; timer = null; }
        changed();
    }
    public void Reset() { Stop(); watch.Reset(); Started = false; changed(); }
    public static string Format(double seconds)
    {
        var value = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}" : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }
}
