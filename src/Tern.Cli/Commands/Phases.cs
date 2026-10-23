using System.Diagnostics;

namespace Tern.Cli.Commands;

/// <summary>How long each step of a command took, for its closing "Finished in" line.</summary>
internal sealed class Phases(Stopwatch watch)
{
    private readonly List<(string Name, TimeSpan Time)> _done = [];
    private TimeSpan _last;

    /// <summary>Ends the step that started when the last one ended.</summary>
    public void End(string name)
    {
        var now = watch.Elapsed;
        _done.Add((name, now - _last));
        _last = now;
    }

    /// <summary>"7.6 s (read 3.1 s, compare 2.0 s, apply 2.5 s)", leaving out steps that took next to no time.</summary>
    public string Summary()
    {
        var shown = _done.Where(p => p.Time >= TimeSpan.FromMilliseconds(50)).ToList();
        var total = SyncRunner.FormatElapsed(watch.Elapsed);
        return shown.Count < 2 ? total : $"{total} ({string.Join(", ", shown.Select(p => $"{p.Name} {SyncRunner.FormatElapsed(p.Time)}"))})";
    }
}
