using System.Text.Json;

namespace Tern.Cli.Common;

/// <summary>
/// Everything a command prints. Results go to stdout and progress, logs and warnings go to stderr
/// (docs/cli-contract.md). With <c>--json</c>, stdout carries exactly one JSON document.
/// </summary>
public sealed class Output(bool json, bool verbose, bool trace)
{
    public bool Json { get; } = json;
    public bool Verbose { get; } = verbose || trace;
    public bool Trace { get; } = trace;

    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>
    /// Whether to colour text: by default, only when the stream is a terminal and <c>NO_COLOR</c>
    /// is unset. Tests set it.
    /// </summary>
    public bool? Colors { get; init; }

    /// <summary>A result line for humans. Suppressed under --json.</summary>
    public void Line(string text = "")
    {
        if (!Json)
        {
            Out.WriteLine(text);
        }
    }

    /// <summary>A result line for humans in an ANSI colour (for example "32", green). Suppressed under --json.</summary>
    public void Line(string text, string color)
    {
        if (!Json)
        {
            Out.WriteLine(Color(color, text, Console.IsOutputRedirected));
        }
    }

    public void Info(string text) => Err.WriteLine(text);

    /// <summary>A progress line in an ANSI colour.</summary>
    public void Info(string text, string color) => Err.WriteLine(Color(color, text));

    /// <summary>Text in an ANSI colour for a stderr line, for a line with several colours; plain when colours are off.</summary>
    public string Paint(string color, string text) => Color(color, text);

    /// <summary>The colour of secondary text: hosts, labels, counts, timings.</summary>
    public const string Dim = "90";

    /// <summary>The colour of an endpoint that is only read from.</summary>
    public const string ReadColor = "36";

    /// <summary>The colour of an endpoint that is written to, and of a mode that writes.</summary>
    public const string WriteColor = "33";

    /// <summary>"dev (https://host)" with the name in <paramref name="color"/> and the host dim.</summary>
    public string PaintEndpoint(string name, string? host, string color = ReadColor) =>
        Paint(color, name) + (host is null ? string.Empty : " " + Paint(Dim, $"({host})"));

    /// <summary>The endpoints already named on stderr, so each is named once.</summary>
    public HashSet<string> Announced { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once a command has printed how long it ran, so it isn't printed twice.</summary>
    public bool ElapsedShown { get; set; }

    /// <summary>Whether <see cref="Page"/> may use a pager; <c>--pager</c> turns it on.</summary>
    public bool UsePager { get; init; }

    /// <summary>
    /// Progress lines, each with an optional colour. When <paramref name="allowPager"/> is set, both
    /// streams are a terminal and the lines don't fit on it, they go through the <see cref="Pager"/>.
    /// </summary>
    public void Page(IReadOnlyList<(string Text, string? Color)> lines, bool allowPager)
    {
        string Render((string Text, string? Color) line) => line.Color is null ? line.Text : Color(line.Color, line.Text);
        if (allowPager && UsePager && !Json && ReferenceEquals(Err, Console.Error) && !Console.IsErrorRedirected && !Console.IsOutputRedirected
            && lines.Count >= TerminalHeight() && Pager.Find() is { } pager)
        {
            Err.WriteLine(Color("90", $"Showing {lines.Count:N0} lines in {Path.GetFileNameWithoutExtension(pager.File)}: Up/Down or Space to scroll, / to search, q to quit."));
            if (Pager.Run(pager, lines.Select(Render)))
            {
                return;
            }
        }
        foreach (var line in lines)
        {
            Err.WriteLine(Render(line));
        }
    }

    private static int TerminalHeight()
    {
        try
        {
            return Console.WindowHeight is > 0 and var height ? height : int.MaxValue;
        }
        catch (IOException)
        {
            return int.MaxValue;
        }
    }

    /// <summary>
    /// A progress line that rewrites itself in place on a terminal. When stderr is redirected, as in
    /// CI or a log file, it becomes a plain line at most every 15 seconds, so a long step still shows
    /// it is alive. Safe to call from several threads.
    /// </summary>
    public void Progress(string text)
    {
        lock (_progressLock)
        {
            if (_progressStopped)
            {
                return;
            }
            if (Console.IsErrorRedirected)
            {
                if (Environment.TickCount64 - _lastLoggedProgress >= 15_000)
                {
                    _lastLoggedProgress = Environment.TickCount64;
                    Err.WriteLine(text);
                }
                return;
            }
            Err.Write($"\r{text}\u001b[K");
            _progressShown = true;
        }
    }

    /// <summary>Clears the progress line, if one is showing, before the next line is written.</summary>
    public void EndProgress()
    {
        lock (_progressLock)
        {
            EndProgressLocked();
        }
    }

    /// <summary>Clears the progress line and ignores later updates, for when a step fails while another still reports.</summary>
    public void StopProgress()
    {
        lock (_progressLock)
        {
            EndProgressLocked();
            _progressStopped = true;
        }
    }

    private void EndProgressLocked()
    {
        if (_progressShown)
        {
            Err.Write("\r\u001b[K");
            _progressShown = false;
        }
    }

    private readonly Lock _progressLock = new();
    private bool _progressShown;
    private bool _progressStopped;
    private long _lastLoggedProgress = Environment.TickCount64;

    public void Warn(string text) => Err.WriteLine(Color("33", text));

    public void Error(string text) => Err.WriteLine(Color("31", text));

    public void Debug(string text)
    {
        if (Verbose)
        {
            Err.WriteLine(Color("90", text));
        }
    }

    /// <summary>
    /// When set, <see cref="Document"/> adds each document to this open JSON array instead of
    /// stdout, so a command made of several steps still prints one document.
    /// </summary>
    public Utf8JsonWriter? Steps { get; set; }

    /// <summary>Writes the command's JSON document to stdout.</summary>
    public void Document(string command, Action<Utf8JsonWriter> write)
    {
        if (!Json)
        {
            return;
        }
        if (Steps is { } steps)
        {
            steps.WriteStartObject();
            steps.WriteString("command", command);
            write(steps);
            steps.WriteEndObject();
            return;
        }
        using var stream = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("command", command);
        write(w);
        w.WriteEndObject();
        w.Flush();
        stream.Write("\n"u8);
    }

    /// <summary>
    /// Writes one bare JSON value to stdout, with no <c>schemaVersion</c> wrapper. Used where the
    /// Official CLI already has <c>--json</c> (the <c>cloud</c> group), so its payload is printed
    /// exactly as existing scripts expect it (docs/cli-contract.md).
    /// </summary>
    public void RawJson(Action<Utf8JsonWriter> writeValue)
    {
        if (!Json)
        {
            return;
        }
        using var stream = JsonStream is null ? Console.OpenStandardOutput() : null;
        var target = JsonStream ?? stream!;
        using var w = new Utf8JsonWriter(target, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writeValue(w);
        w.Flush();
        target.Write("\n"u8);
    }

    /// <summary>Where <see cref="RawJson"/> writes instead of stdout; tests set it.</summary>
    public Stream? JsonStream { get; init; }

    /// <summary>Colours text bound for stderr, or for stdout when <paramref name="redirected"/> says whether it is redirected.</summary>
    private string Color(string code, string text, bool? redirected = null) =>
        (Colors ?? (!(redirected ?? Console.IsErrorRedirected) && Environment.GetEnvironmentVariable("NO_COLOR") is null)) ? $"\u001b[{code}m{text}\u001b[0m" : text;
}

/// <summary>A failure the user can act on; its message is shown without a stack trace.</summary>
public sealed class CliException(string message, int exitCode = ExitCodes.Failure) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;

    /// <summary>A saved Plan no longer matches the target.</summary>
    public const int PlanOutOfDate = 3;

    /// <summary>A what-if or diff found differences and was asked to fail on them.</summary>
    public const int ChangesFound = 4;
}
