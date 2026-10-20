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

    /// <summary>Writes the command's JSON document to stdout.</summary>
    public void Document(string command, Action<Utf8JsonWriter> write)
    {
        if (!Json)
        {
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
