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

    /// <summary>A result line for humans. Suppressed under --json.</summary>
    public void Line(string text = "")
    {
        if (!Json)
        {
            Out.WriteLine(text);
        }
    }

    public void Info(string text) => Err.WriteLine(text);

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

    private static bool UseColor => !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Color(string code, string text) => UseColor ? $"\u001b[{code}m{text}\u001b[0m" : text;
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
