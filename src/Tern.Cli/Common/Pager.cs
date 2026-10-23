using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Tern.Cli.Common;

/// <summary>
/// Shows long output a screen at a time, as git does: through <c>TERN_PAGER</c>, then <c>PAGER</c>,
/// then <c>less</c> from the PATH or from Git for Windows. An empty value or <c>cat</c> turns it off.
/// </summary>
public static class Pager
{
    /// <summary>The pager to run, or null when there is none or it is turned off.</summary>
    public static (string File, string Arguments)? Find()
    {
        if ((Environment.GetEnvironmentVariable("TERN_PAGER") ?? Environment.GetEnvironmentVariable("PAGER")) is { } configured)
        {
            configured = configured.Trim();
            return configured is "" or "cat" ? null : Split(configured);
        }
        return (OnPath("less") ?? GitLess()) is { } less ? (less, string.Empty) : null;
    }

    /// <summary>Splits a command into its program, which may be quoted, and its arguments.</summary>
    internal static (string File, string Arguments) Split(string command)
    {
        if (command.StartsWith('"') && command.IndexOf('"', 1) is var close and > 0)
        {
            return (command[1..close], command[(close + 1)..].Trim());
        }
        var space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].Trim());
    }

    /// <summary>
    /// Writes the lines to the pager and waits for it to close. False when it could not be started,
    /// so the caller prints the lines itself.
    /// </summary>
    public static bool Run((string File, string Arguments) pager, IEnumerable<string> lines)
    {
        var info = new ProcessStartInfo(pager.File, WithPrompt(pager.File, pager.Arguments))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // As git does: quit when it fits on a screen, keep colours, leave the text on screen afterwards.
        if (Environment.GetEnvironmentVariable("LESS") is null)
        {
            info.Environment["LESS"] = "FRX";
        }
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception)
        {
            return false;
        }
        if (process is null)
        {
            return false;
        }
        // Ctrl+C belongs to the pager while it is open.
        ConsoleCancelEventHandler hold = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += hold;
        try
        {
            using (process)
            {
                try
                {
                    process.StandardInput.NewLine = "\n";
                    foreach (var line in lines)
                    {
                        process.StandardInput.WriteLine(line);
                    }
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Quit before reading everything.
                }
                process.WaitForExit();
            }
        }
        finally
        {
            Console.CancelKeyPress -= hold;
        }
        return true;
    }

    /// <summary>
    /// The arguments for <c>less</c> with a status line that says where you are and which keys to
    /// press, unless the command already sets a prompt. Other pagers get theirs unchanged.
    /// </summary>
    internal static string WithPrompt(string file, string arguments) =>
        !Path.GetFileNameWithoutExtension(file).Equals("less", StringComparison.OrdinalIgnoreCase) || arguments.Contains("-P", StringComparison.Ordinal)
            ? arguments
            : $"{arguments} \"{Prompt}\"".TrimStart();

    /// <summary>
    /// "Lines 1-40 of 4812  (Up/Down or Space scroll, / search, q quit)" in less's prompt language,
    /// where <c>?L … .</c> leaves out the total until less knows it.
    /// </summary>
    internal const string Prompt = "-PsLines %lt-%lb?L of %L.  (Up/Down or Space scroll, / search, q quit)";

    private static string? OnPath(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir.Trim('"'), file);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>The <c>less</c> that Git for Windows installs, which is rarely on the PATH.</summary>
    private static string? GitLess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        List<string> candidates = [];
        if (OnPath("git") is { } git && Path.GetDirectoryName(git) is { } dir)
        {
            candidates.Add(Path.Combine(dir, "..", "usr", "bin", "less.exe"));
            candidates.Add(Path.Combine(dir, "..", "..", "usr", "bin", "less.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "less.exe"));
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}
