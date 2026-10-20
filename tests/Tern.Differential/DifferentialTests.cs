using System.Diagnostics;
using System.Text.RegularExpressions;
using Tern.Core.Items;

namespace Tern.Differential;

/// <summary>
/// The side-by-side tests against the Official CLI (docs/testing.md). They need a CM and the
/// Official CLI, so they run only on a developer machine with TERN_DIFFERENTIAL_FIXTURE and
/// TERN_EXE set; elsewhere they are skipped.
/// </summary>
public class DifferentialTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void The_snapshot_normaliser_removes_every_statistics_field()
    {
        // A snapshot comparison is only as good as its list of fields that change on every save.
        var script = File.ReadAllText(Path.Combine(Repo(), "tests", "differential", "normalize.py"));
        var listed = Regex.Matches(script, "\"([0-9a-f-]{36})\"").Select(m => Guid.Parse(m.Groups[1].Value)).ToHashSet();
        Assert.All(WellKnownFields.Statistics.Where(f => f != WellKnownFields.LastRun), f => Assert.Contains(f, listed));
    }

    [Fact]
    public void Push_matches_the_Official_CLI()
    {
        var fixture = Environment.GetEnvironmentVariable("TERN_DIFFERENTIAL_FIXTURE");
        var tern = Environment.GetEnvironmentVariable("TERN_EXE");
        Assert.SkipWhen(fixture is null || tern is null, "TERN_DIFFERENTIAL_FIXTURE and TERN_EXE are not set.");

        var script = Path.Combine(Repo(), "tests", "differential", "push-diff.sh");
        using var process = Process.Start(new ProcessStartInfo("bash", [script, fixture!, tern!])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        TestContext.Current.SendDiagnosticMessage(output);
        Assert.True(process.ExitCode == 0, output);
    }
}
