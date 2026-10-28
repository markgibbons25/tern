using System.Globalization;
using System.Text.Json;

namespace Tern.Cli.Common;

/// <summary>
/// Tells the user when NuGet has a newer Tern, after a command or when <c>tern</c> runs with none.
/// NuGet is asked at most once a day, in the background while the command runs, and any failure is ignored, so no command waits on it or fails because
/// of it. It stays quiet for <c>--json</c>, redirected output, CI, and <c>TERN_NO_UPDATE_CHECK</c>.
/// </summary>
public sealed class UpdateCheck
{
    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/tern.cli/index.json";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1.5);

    // How long a finished command waits for a request still in flight; NuGet usually answers in about 0.2 s.
    private static readonly TimeSpan GraceAfterCommand = TimeSpan.FromMilliseconds(300);

    private readonly string _current;
    private readonly Task<string?> _latest;

    private UpdateCheck(string current, Task<string?> latest)
    {
        _current = current;
        _latest = latest;
    }

    public static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tern", "update-check.json");

    /// <summary>Starts the check for a command, or returns null when it shouldn't run.</summary>
    public static UpdateCheck? Start(Output output)
    {
        var current = TernApp.Version;
        if (output.Json || Console.IsErrorRedirected || current == "0.0.0"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERN_NO_UPDATE_CHECK")))
        {
            return null;
        }
        var cached = ReadCache(CacheFile);
        if (cached is { } c && IsFresh(c.CheckedAt, DateTimeOffset.UtcNow))
        {
            return new UpdateCheck(current, Task.FromResult(c.Latest));
        }
        return new UpdateCheck(current, Task.Run(() => FetchLatestAsync(current)));
    }

    /// <summary>Prints the notice, set off by a blank line, when a newer version is known by now; never throws.</summary>
    public async Task ReportAsync(Output output)
    {
        try
        {
            if (await Task.WhenAny(_latest, Task.Delay(GraceAfterCommand)) != _latest || await _latest is not { } latest)
            {
                return;
            }
            if (IsNewer(latest, _current))
            {
                output.Info(string.Empty);
                output.Info($"Tern {latest} is available (you have {_current}).", "1;33");
                output.Info($"Update with: {output.Paint(Output.ReadColor, "dotnet tool update --global Tern.Cli")}");
            }
        }
        catch (Exception)
        {
            // A version check never gets in the way of the command.
        }
    }

    private static async Task<string?> FetchLatestAsync(string current)
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            await using var stream = await http.GetStreamAsync(new Uri(IndexUrl));
            using var doc = await JsonDocument.ParseAsync(stream);
            var versions = doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()).OfType<string>();
            var latest = Latest(versions, includePrerelease: current.Contains('-', StringComparison.Ordinal));
            WriteCache(CacheFile, DateTimeOffset.UtcNow, latest);
            return latest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool IsFresh(DateTimeOffset checkedAt, DateTimeOffset now) => now - checkedAt < CheckEvery && checkedAt <= now;

    /// <summary>The highest version, leaving out prereleases unless <paramref name="includePrerelease"/>.</summary>
    internal static string? Latest(IEnumerable<string> versions, bool includePrerelease) =>
        versions.Where(v => Parse(v) is not null && (includePrerelease || !v.Contains('-', StringComparison.Ordinal)))
            .Aggregate((string?)null, (best, v) => best is null || IsNewer(v, best) ? v : best);

    /// <summary>Whether <paramref name="candidate"/> is a later SemVer than <paramref name="current"/>.</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        if (Parse(candidate) is not { } a || Parse(current) is not { } b)
        {
            return false;
        }
        var byNumber = a.Number.CompareTo(b.Number);
        if (byNumber != 0)
        {
            return byNumber > 0;
        }
        // A release is later than any of its prereleases; prereleases compare by label.
        return (a.Pre, b.Pre) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            _ => string.CompareOrdinal(a.Pre, b.Pre) > 0,
        };
    }

    private static (Version Number, string? Pre)? Parse(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var core = plus < 0 ? version : version[..plus];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var number = dash < 0 ? core : core[..dash];
        return Version.TryParse(number, out var v) ? (v, dash < 0 ? null : core[(dash + 1)..]) : null;
    }

    internal static (DateTimeOffset CheckedAt, string? Latest)? ReadCache(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var checkedAt = DateTimeOffset.Parse(doc.RootElement.GetProperty("checkedAt").GetString()!, CultureInfo.InvariantCulture);
            var latest = doc.RootElement.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            return (checkedAt, latest);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void WriteCache(string file, DateTimeOffset checkedAt, string? latest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = File.Create(file);
            using var w = new Utf8JsonWriter(stream);
            w.WriteStartObject();
            w.WriteString("checkedAt", checkedAt.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("latest", latest);
            w.WriteEndObject();
        }
        catch (Exception)
        {
            // Another tern writing at the same time, or a read-only profile: check again next run.
        }
    }
}
