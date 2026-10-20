using System.CommandLine;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Planning;
using Tern.Core.Trees;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>ser watch</c>: a continuous Pull. It polls the CM's Change history and re-pulls only the
/// Includes whose items changed. As in the Official CLI, it stops when an Item file changes on disk
/// unless <c>--allow-file-changes</c> is set, because a two-way edit would corrupt the tree.
/// </summary>
public static class WatchCommand
{
    public static Command Build()
    {
        var environment = CommonOptions.EnvironmentName();
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var skipPull = new Option<bool>("--skip-pull", "-s") { Description = "Skips pulling data from Sitecore before starting the watcher" };
        var allowFileChanges = new Option<bool>("--allow-file-changes") { Description = "Don't stop watch if files change. May result in tree corruption if underlying state is changed" };
        var interval = new Option<int>("--interval") { Description = "Milliseconds between polls of the CM (minimum 1000)", DefaultValueFactory = _ => 2000 };
        var cmd = new Command("watch", "Watch a Sitecore instance and pull changes as they happen") { environment, include, exclude, skipPull, allowFileChanges, interval };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var output = session.Output;
            var request = new SyncRequest(parse.GetValue(environment), parse.GetValue(include)!, parse.GetValue(exclude)!, false, false, false, null, false, []);
            if (!parse.GetValue(skipPull))
            {
                var exit = await SyncRunner.PullAsync(session, request, token);
                if (exit != ExitCodes.Success)
                {
                    return exit;
                }
            }
            var modules = session.SelectModules(request.Include, request.Exclude);
            var includes = modules.SelectMany(m => m.Includes).ToList();
            var excluded = session.ExcludedFields(modules).ToHashSet();
            var endpoint = session.Endpoint(request.Environment);
            var api = session.Serialization(endpoint);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var writing = false;
            var watchers = new List<FileSystemWatcher>();
            if (!parse.GetValue(allowFileChanges))
            {
                foreach (var root in includes.Select(i => i.PhysicalRoot).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var w = new FileSystemWatcher(root, "*.yml") { IncludeSubdirectories = true, EnableRaisingEvents = true };
                    void OnChange(object? s, FileSystemEventArgs e)
                    {
                        if (!Volatile.Read(ref writing))
                        {
                            output.Warn($"{e.FullPath} changed on disk. Stopping watch so the tree is not corrupted; pass --allow-file-changes to keep watching.");
                            stop.Cancel();
                        }
                    }
                    w.Changed += OnChange;
                    w.Created += OnChange;
                    w.Deleted += OnChange;
                    w.Renamed += (s, e) => OnChange(s, e);
                    watchers.Add(w);
                }
            }

            var timestamp = await api.GetHistoryTimestampAsync(token);
            output.Info($"Watching {endpoint.Host} for changes. Press Ctrl+C to stop.");
            var delay = TimeSpan.FromMilliseconds(Math.Max(1000, parse.GetValue(interval)));
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(delay, stop.Token);
                    var (next, entries) = await api.GetHistoryAsync(timestamp, stop.Token);
                    timestamp = next;
                    var affected = includes.Where(i => entries.Any(e => e.Database.Equals(i.Database, StringComparison.OrdinalIgnoreCase) && Touches(i, e))).ToList();
                    if (affected.Count == 0)
                    {
                        continue;
                    }
                    Volatile.Write(ref writing, true);
                    try
                    {
                        var files = new FileItemTree(id => !excluded.Contains(id), index: session.FileIndex(excluded));
                        var cm = new CmItemTree(api, [.. excluded]);
                        await cm.PrefetchAsync(affected, stop.Token);
                        var engine = new SyncEngine(cm, files, new FileApplier(files, id => !excluded.Contains(id)), PlanDirection.Pull);
                        var result = await engine.PlanAsync(affected, force: true, stop.Token);
                        foreach (var op in result.Plan.Operations)
                        {
                            output.Line($"[{op.Database}] {SyncRunner.Describe(op)}");
                        }
                        await engine.ApplyAsync(result.Plan, affected, checkPreconditions: false, stop.Token);
                        // Give the file watcher time to deliver our own writes before listening again.
                        await Task.Delay(250, stop.Token);
                    }
                    finally
                    {
                        Volatile.Write(ref writing, false);
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return ExitCodes.Failure;
            }
            catch (OperationCanceledException)
            {
                output.Info("Stopped watching.");
            }
            finally
            {
                watchers.ForEach(w => w.Dispose());
            }
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static bool Touches(Include include, HistoryEntry entry)
    {
        bool Covers(string? path) => !string.IsNullOrEmpty(path) && path.StartsWith('/') && ItemPath.Parse(path).IsDescendantOrSelfOf(include.Path);
        return Covers(entry.Path) || Covers(entry.OldPath);
    }
}
