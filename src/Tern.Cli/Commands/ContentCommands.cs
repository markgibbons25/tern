using System.CommandLine;
using System.Net;
using System.Text.Json;

using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Transfer;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>content sync</c> and <c>content diff</c>: move or compare content between two Endpoints,
/// one Module, a content Module and its media twin, or one item path at a time.
/// </summary>
public static class ContentCommands
{
    private const int Attempts = 3;

    /// <summary>How long to wait before trying a failed step again; tests shorten it.</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public static Command Build()
    {
        var content = new Command("content", "Copy and compare content between environments");
        content.Subcommands.Add(Sync());
        content.Subcommands.Add(Diff());
        content.Subcommands.Add(Transfer());
        return content;
    }

    private static Command Transfer()
    {
        var o = new ContentOptions();
        var merge = new Option<string>("--merge") { Description = "What to do with items the target already has: OverrideExistingItem (default), KeepExistingItem, or OverrideExistingTree, which deletes each transferred tree in the target first", DefaultValueFactory = _ => nameof(MergeStrategy.OverrideExistingItem) };
        var noConsume = new Option<bool>("--no-consume") { Description = "Copy the .raif files to the target without loading them into its database" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm a destructive merge strategy" };
        var whatIf = new Option<bool>("--what-if", "-w") { Description = "List the data trees that would be transferred, without transferring them" };
        var wait = new Option<bool>("--wait") { Description = "Wait until the target has written the items to its database (minutes), not only until they are available (seconds)" };
        var cmd = new Command("transfer", "Copy content between SitecoreAI environments with the Content Transfer API, CM to CM") { merge, noConsume, yes, whatIf, wait };
        o.AddTo(cmd);
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, token) =>
        {
            if (!Enum.TryParse<MergeStrategy>(parse.GetValue(merge), ignoreCase: true, out var strategy))
            {
                throw new CliException($"--merge must be one of {string.Join(", ", Enum.GetNames<MergeStrategy>())}.");
            }
            return TransferRunner.RunAsync(session, o.Read(parse) with { WhatIf = parse.GetValue(whatIf) }, new TransferOptions(strategy, parse.GetValue(noConsume), parse.GetValue(yes), parse.GetValue(wait)), token);
        }, ct));
        return cmd;
    }

    private static Command Sync()
    {
        var o = new ContentOptions();
        var skipPush = new Option<bool>("--skip-push") { Description = "Pull from the source only, so the Item files can be reviewed before a later --push-only" };
        var pushOnly = new Option<bool>("--push-only") { Description = "Push the Item files already on disk to the target, without pulling first" };
        var whatIf = new Option<bool>("--what-if", "-w") { Description = "Show what would change, without changing files or the target" };
        var cmd = new Command("sync", "Pull content from the source endpoint to Item files, then push those files to the target endpoint") { skipPush, pushOnly, whatIf };
        o.AddTo(cmd);
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, token) =>
            SyncAsync(session, o.Read(parse) with { SkipPush = parse.GetValue(skipPush), PushOnly = parse.GetValue(pushOnly), WhatIf = parse.GetValue(whatIf) }, token), ct));
        return cmd;
    }

    private static Command Diff()
    {
        var o = new ContentOptions();
        var push = new Option<bool>("--push") { Description = "Apply the differences to the target (diff + push)" };
        var cmd = new Command("diff", "Compare content between the source and target endpoints, without Item files") { push };
        o.AddTo(cmd);
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, token) =>
            DiffAsync(session, o.Read(parse) with { Push = parse.GetValue(push) }, token), ct));
        return cmd;
    }

    public static async Task<int> SyncAsync(Session session, ContentRequest request, CancellationToken ct)
    {
        if (request.PushOnly && request.SkipPush)
        {
            throw new CliException("--push-only and --skip-push cannot be used together.");
        }
        var sourceName = request.PushOnly ? null : request.Source ?? throw new CliException("--source is required unless you use --push-only.");
        var targetName = request.SkipPush ? null : request.Target ?? throw new CliException("--target is required unless you use --skip-push.");
        var (modules, scope) = Resolve(session, request);
        var source = sourceName is null ? null : session.Endpoint(sourceName);
        var target = targetName is null ? null : session.Endpoint(targetName);
        if (source is not null && target is not null && source.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("The source and target cannot be the same endpoint.");
        }
        if (target is not null && !target.AllowWrite && !request.WhatIf)
        {
            throw new CliException($"Endpoint '{target.Name}' does not allow writes. Log in again with --allow-write true to push to it.");
        }

        var output = session.Output;
        output.Info("Content sync");
        if (source is not null)
        {
            output.Info($"  Source: {source.Name} ({source.Host})");
        }
        if (target is not null)
        {
            output.Info($"  Target: {target.Name} ({target.Host})");
        }
        output.Info($"  Scope:  {scope}");
        output.Info(string.Empty);

        if (request.PushOnly && !HasItemFiles(modules))
        {
            throw new CliException("There are no Item files for these modules. Pull them first, without --push-only.");
        }

        var (exit, steps) = await CaptureStepsAsync(output, async () =>
        {
            if (request.WhatIf && source is not null && target is not null)
            {
                // A what-if pull writes no files, so a what-if push from them would show stale
                // changes. Comparing the two CMs shows what the sync would carry across.
                output.Info($"What if: comparing {source.Name} with {target.Name} directly; nothing is pulled or pushed.");
                var diff = new DiffRequest(source.Name, target.Name, [], [], null, null, null, Push: false, null) { Modules = modules };
                return await RetryAsync(output, $"the comparison of {source.Name} with {target.Name}", () => PushRunner.DiffAsync(session, diff, ct), ct);
            }
            if (source is not null)
            {
                output.Info($"Pulling from {source.Name}...");
                var pull = new SyncRequest(source.Name, [], [], request.WhatIf, SkipValidation: false, Force: false, PlanOut: null, Publish: false, PublishTargets: []) { Modules = modules };
                var code = await RetryAsync(output, $"the pull from {source.Name}", () => SyncRunner.PullAsync(session, pull, ct), ct);
                if (code != ExitCodes.Success)
                {
                    return code;
                }
            }
            if (target is not null)
            {
                // Media goes first, then the content that links to it, each as its own push so a
                // slow gateway never has to carry the whole set at once.
                var batches = new[] { modules.Where(ContentScope.IsMedia).ToList(), modules.Where(m => !ContentScope.IsMedia(m)).ToList() }.Where(b => b.Count > 0);
                foreach (var batch in batches)
                {
                    var label = string.Join(", ", batch.Select(m => m.Namespace));
                    output.Info($"Pushing {label} to {target.Name}...");
                    var push = new SyncRequest(target.Name, [], [], request.WhatIf, SkipValidation: false, Force: true, PlanOut: null, Publish: false, PublishTargets: []) { Modules = batch };
                    var code = await RetryAsync(output, $"the push of {label} to {target.Name}", () => PushRunner.PushAsync(session, push, ct), ct);
                    if (code != ExitCodes.Success)
                    {
                        return code;
                    }
                }
            }
            return ExitCodes.Success;
        });

        output.Document("content sync", w =>
        {
            w.WriteString("source", source?.Name);
            w.WriteString("target", target?.Name);
            WriteModules(w, modules);
            w.WritePropertyName("steps");
            w.WriteRawValue(steps);
        });
        if (exit == ExitCodes.Success)
        {
            output.Info(request.WhatIf ? "Content sync what-if complete. Nothing was changed." : "Content sync complete.");
        }
        return exit;
    }

    public static async Task<int> DiffAsync(Session session, ContentRequest request, CancellationToken ct)
    {
        var sourceName = request.Source ?? throw new CliException("--source is required.");
        var targetName = request.Target ?? throw new CliException("--target is required.");
        var (modules, scope) = Resolve(session, request);
        var source = session.Endpoint(sourceName);
        var target = session.Endpoint(targetName);
        if (source.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("The source and target cannot be the same endpoint.");
        }

        var output = session.Output;
        output.Info("Content diff");
        output.Info($"  Source: {source.Name} ({source.Host})");
        output.Info($"  Target: {target.Name} ({target.Host})");
        output.Info($"  Scope:  {scope}");
        output.Info(request.Push ? $"  Mode:   diff and apply (writes to {target.Name})" : "  Mode:   diff only (read-only)");

        var diff = new DiffRequest(source.Name, target.Name, [], [], null, null, null, request.Push, null) { Modules = modules };
        var (exit, steps) = await CaptureStepsAsync(output, () => PushRunner.DiffAsync(session, diff, ct));
        output.Document("content diff", w =>
        {
            w.WriteString("source", source.Name);
            w.WriteString("target", target.Name);
            w.WriteBoolean("push", request.Push);
            WriteModules(w, modules);
            w.WritePropertyName("steps");
            w.WriteRawValue(steps);
        });
        return exit;
    }

    /// <summary>The Modules a request covers, and a line that describes them.</summary>
    internal static (IReadOnlyList<Module> Modules, string Description) Resolve(Session session, ContentRequest request)
    {
        if (request.Scope is not null && request.Path is null)
        {
            throw new CliException("--scope needs --path.");
        }
        if (request.Path is { } path)
        {
            if (request.Module is not null)
            {
                throw new CliException("--module and --path cannot be used together; --path makes a module of its own.");
            }
            if (request.SkipMedia)
            {
                throw new CliException("--skip-media has no effect with --path; a path is one include, not a set of content and media modules.");
            }
            var scope = TreeScope.ItemAndDescendants;
            if (request.Scope is { } s && !Enum.TryParse(s, ignoreCase: true, out scope))
            {
                throw new CliException($"--scope must be one of {string.Join(", ", Enum.GetNames<TreeScope>())}.");
            }
            var module = ContentScope.ForPath(session, path, request.Database, scope);
            return ([module], $"{path} ({scope}, {request.Database})");
        }
        if (request.Module is { } name)
        {
            var matched = ContentScope.Match(ContentScope.Candidates(session), name, request.SkipMedia);
            return (ContentScope.MediaFirst(ConfigurationLoader.Validate(matched, session.Output.Warn)), Describe(matched));
        }
        var all = ContentScope.MediaFirst(ConfigurationLoader.Validate(ContentScope.All(session.AllModules, request.SkipMedia), session.Output.Warn));
        return (all, (request.SkipMedia ? "every module in sitecore.json except media: " : "every module in sitecore.json: ") + Describe(all));
    }

    private static string Describe(IEnumerable<Module> modules) =>
        string.Join(", ", modules.Select(m => $"{Path.GetFileName(m.SourcePath)} ({m.Namespace})"));

    private static bool HasItemFiles(IReadOnlyList<Module> modules)
    {
        var includes = modules.SelectMany(m => m.Includes).ToList();
        // A Module of roles or users alone has no Item files to look for.
        return includes.Count == 0 || includes.Any(i => Directory.Exists(i.PhysicalRoot) && Directory.EnumerateFiles(i.PhysicalRoot, "*.yml", SearchOption.AllDirectories).Any());
    }

    private static void WriteModules(Utf8JsonWriter w, IReadOnlyList<Module> modules)
    {
        w.WriteStartArray("modules");
        foreach (var m in modules)
        {
            w.WriteStartObject();
            w.WriteString("namespace", m.Namespace);
            w.WriteString("file", m.SourcePath);
            w.WriteBoolean("media", ContentScope.IsMedia(m));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>Runs the steps with each step's JSON document gathered into one array.</summary>
    private static async Task<(int Exit, byte[] Steps)> CaptureStepsAsync(Output output, Func<Task<int>> run)
    {
        using var buffer = new MemoryStream();
        int exit;
        using (var steps = new Utf8JsonWriter(buffer))
        {
            steps.WriteStartArray();
            output.Steps = steps;
            try
            {
                exit = await run();
            }
            finally
            {
                output.Steps = null;
            }
            steps.WriteEndArray();
        }
        return (exit, buffer.ToArray());
    }

    /// <summary>
    /// Runs a pull or push again when the CM or its gateway timed out. Both are safe to repeat:
    /// each one works out afresh what still differs.
    /// </summary>
    internal static async Task<int> RetryAsync(Output output, string what, Func<Task<int>> step, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await step();
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex, ct))
            {
                output.Warn($"{what} failed ({ex.Message}). Trying again in {RetryDelay.TotalSeconds:0} s (attempt {attempt + 1} of {Attempts})...");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    internal static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        ManagementException { StatusCode: { } status } => status is (int)HttpStatusCode.BadGateway or (int)HttpStatusCode.ServiceUnavailable or (int)HttpStatusCode.GatewayTimeout or 520 or 522 or 524,
        HttpRequestException => true,
        // HttpClient reports its own timeout as a cancellation nobody asked for.
        TaskCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };
}

/// <summary>The options <c>content sync</c> and <c>content diff</c> share.</summary>
public sealed class ContentOptions
{
    private readonly Option<string?> _source = new("--source", "-s") { Description = "Named endpoint to read content from" };
    private readonly Option<string?> _target = new("--target", "--destination", "-d") { Description = "Named endpoint to write content to, or to compare with" };
    private readonly Option<string?> _module = new("--module", "-m") { Description = "One module instead of every module in sitecore.json: its file name, namespace, or the end of its file name. A content module and its media twin both match" };
    private readonly Option<string?> _path = new("--path", "-p") { Description = "One item path instead of modules" };
    private readonly Option<string?> _scope = new("--scope") { Description = "With --path: ItemAndDescendants (default), ItemAndChildren, SingleItem or DescendantsOnly" };
    private readonly Option<string> _database = new("--database") { Description = "With --path: the database on both sides", DefaultValueFactory = _ => "master" };
    private readonly Option<bool> _skipMedia = new("--skip-media", "--exclude-media") { Description = "Leave out every module whose includes are all in the media library" };

    public void AddTo(Command cmd)
    {
        cmd.Options.Add(_source);
        cmd.Options.Add(_target);
        cmd.Options.Add(_module);
        cmd.Options.Add(_path);
        cmd.Options.Add(_scope);
        cmd.Options.Add(_database);
        cmd.Options.Add(_skipMedia);
    }

    public ContentRequest Read(ParseResult parse) => new()
    {
        Source = parse.GetValue(_source),
        Target = parse.GetValue(_target),
        Module = parse.GetValue(_module),
        Path = parse.GetValue(_path),
        Scope = parse.GetValue(_scope),
        Database = parse.GetValue(_database)!,
        SkipMedia = parse.GetValue(_skipMedia),
    };
}

public sealed record ContentRequest
{
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Module { get; init; }
    public string? Path { get; init; }
    public string? Scope { get; init; }
    public string Database { get; init; } = "master";
    public bool SkipMedia { get; init; }
    public bool SkipPush { get; init; }
    public bool PushOnly { get; init; }
    public bool WhatIf { get; init; }
    public bool Push { get; init; }
}
