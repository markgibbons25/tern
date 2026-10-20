using System.Globalization;
using System.Text.Json;

namespace Tern.Cli.Common;

/// <summary>
/// Tern's own settings (ADR 0011). Highest precedence first: flags, <c>TERN_*</c> environment
/// variables, the project's <c>tern.json</c>, the user's <c>~/.config/tern/settings.json</c>, defaults.
/// Flags are applied by the commands that take them.
/// </summary>
public sealed class TernSettings
{
    public int AliasesPerRequest { get; private set; } = 26;
    public int ParallelRequests { get; private set; } = 8;
    public int WriteBatchSize { get; private set; } = 1000;

    /// <summary>
    /// Read subtrees of at least this many items as parallel requests; 0 never splits. Unset, it
    /// depends on the CM (see <see cref="Session.Serialization"/>).
    /// </summary>
    public int? SplitThreshold { get; private set; }
    public bool UseKeychain { get; private set; }

    /// <summary>Keep an index of Item files outside the project so unchanged files aren't read again.</summary>
    public bool FileIndex { get; private set; } = true;

    public static TernSettings Load(string? projectDirectory)
    {
        var settings = new TernSettings();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        settings.Apply(Path.Combine(home, ".config", "tern", "settings.json"));
        if (projectDirectory is not null)
        {
            settings.Apply(Path.Combine(projectDirectory, "tern.json"));
        }
        settings.ApplyEnvironment();
        return settings;
    }

    private void Apply(string file)
    {
        if (!File.Exists(file))
        {
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            Set(p.Name, p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                _ => p.Value.GetRawText(),
            });
        }
    }

    private void ApplyEnvironment()
    {
        Set("aliasesPerRequest", Environment.GetEnvironmentVariable("TERN_ALIASES_PER_REQUEST"));
        Set("parallelRequests", Environment.GetEnvironmentVariable("TERN_PARALLEL_REQUESTS"));
        Set("writeBatchSize", Environment.GetEnvironmentVariable("TERN_WRITE_BATCH_SIZE"));
        Set("splitThreshold", Environment.GetEnvironmentVariable("TERN_SPLIT_THRESHOLD"));
        Set("useKeychain", Environment.GetEnvironmentVariable("TERN_USE_KEYCHAIN"));
        Set("fileIndex", Environment.GetEnvironmentVariable("TERN_FILE_INDEX"));
    }

    private void Set(string name, string? value)
    {
        if (value is null)
        {
            return;
        }
        switch (name.ToLowerInvariant())
        {
            case "aliasesperrequest" when int.TryParse(value, CultureInfo.InvariantCulture, out var a) && a > 0:
                AliasesPerRequest = a;
                break;
            case "parallelrequests" when int.TryParse(value, CultureInfo.InvariantCulture, out var p) && p > 0:
                ParallelRequests = p;
                break;
            case "writebatchsize" when int.TryParse(value, CultureInfo.InvariantCulture, out var w) && w > 0:
                WriteBatchSize = w;
                break;
            case "splitthreshold" when int.TryParse(value, CultureInfo.InvariantCulture, out var t) && t >= 0:
                SplitThreshold = t;
                break;
            case "usekeychain" when bool.TryParse(value, out var k):
                UseKeychain = k;
                break;
            case "fileindex" when bool.TryParse(value, out var f):
                FileIndex = f;
                break;
        }
    }
}
