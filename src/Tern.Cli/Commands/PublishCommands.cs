using System.CommandLine;

using Tern.Cli.Common;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary><c>publish</c>, <c>index</c> and <c>dbcleanup</c>: the CM job commands.</summary>
public static class PublishCommands
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    public static Command Build()
    {
        var environment = CommonOptions.EnvironmentName();
        var languages = new Option<string[]>("--languages", "-l") { Description = "Languages to publish", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var path = new Option<string?>("--path", "-p") { Description = "Item path to publish (deprecated; use `publish item`)" };
        var republish = new Option<bool>("--republish", "-r") { Description = "Republish everything instead of only changes" };
        var targets = new Option<string[]>("--targets", "--pt") { Description = "Publishing targets", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var cmd = new Command("publish", "Publish the master database") { environment, languages, path, republish, targets };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = new PublishingApi(session.Management(session.Endpoint(parse.GetValue(environment))));
            var langs = Split(parse.GetValue(languages)!);
            var ts = Targets(parse.GetValue(targets)!);
            var jobs = await Task.WhenAll(ts.Select(t => RunAndWaitAsync(api, session.Output, () => api.PublishAsync(parse.GetValue(path), langs, [], parse.GetValue(republish), t, token), token)));
            Report(session.Output, "publish", jobs);
            return jobs.Any(j => j.IsFailed) ? ExitCodes.Failure : ExitCodes.Success;
        }, ct));

        var itemEnv = CommonOptions.EnvironmentName();
        var itemPath = new Option<string>("--path", "-p") { Description = "Item path to publish", Required = true };
        var sub = new Option<bool>("--subitems", "-sub") { Description = "Publish subitems too" };
        var related = new Option<bool>("--related", "-rel") { Description = "Publish related items too" };
        var itemLanguages = new Option<string[]>("--languages", "-l") { Description = "Languages to publish", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var itemRepublish = new Option<bool>("--republish", "-r") { Description = "Republish instead of only changes" };
        var itemTargets = new Option<string[]>("--targets", "--pt") { Description = "Publishing targets", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var item = new Command("item", "Publish one item") { itemEnv, itemPath, sub, related, itemLanguages, itemRepublish, itemTargets };
        item.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = new PublishingApi(session.Management(session.Endpoint(parse.GetValue(itemEnv))));
            var langs = Split(parse.GetValue(itemLanguages)!);
            var ts = Targets(parse.GetValue(itemTargets)!);
            var jobs = await Task.WhenAll(ts.Select(t => RunAndWaitAsync(api, session.Output, () => api.PublishItemAsync(parse.GetValue(itemPath)!, langs, parse.GetValue(itemRepublish), t, parse.GetValue(related), parse.GetValue(sub), token), token)));
            Report(session.Output, "publish item", jobs);
            return jobs.Any(j => j.IsFailed) ? ExitCodes.Failure : ExitCodes.Success;
        }, ct));
        cmd.Subcommands.Add(item);

        var listEnv = CommonOptions.EnvironmentName();
        var list = new Command("list-targets", "List publishing targets") { listEnv };
        list.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = new PublishingApi(session.Management(session.Endpoint(parse.GetValue(listEnv))));
            var names = await api.TargetsAsync(token);
            session.Output.Document("publish list-targets", w =>
            {
                w.WriteStartArray("targets");
                foreach (var n in names)
                {
                    w.WriteStringValue(n);
                }
                w.WriteEndArray();
            });
            foreach (var n in names)
            {
                session.Output.Line(n);
            }
            return ExitCodes.Success;
        }, ct));
        cmd.Subcommands.Add(list);
        return cmd;
    }

    public static Command BuildIndex()
    {
        var index = new Command("index", "Manage search indexes");

        var listEnv = CommonOptions.EnvironmentName();
        var list = new Command("list", "List search indexes") { listEnv };
        list.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var names = await new IndexingApi(session.Management(session.Endpoint(parse.GetValue(listEnv)))).ListAsync(token);
            session.Output.Document("index list", w =>
            {
                w.WriteStartArray("indexes");
                names.ToList().ForEach(w.WriteStringValue);
                w.WriteEndArray();
            });
            names.ToList().ForEach(n => session.Output.Line(n));
            return ExitCodes.Success;
        }, ct));
        index.Subcommands.Add(list);

        var statEnv = CommonOptions.EnvironmentName();
        var stat = new Command("statistic", "Show search index statistics") { statEnv };
        stat.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var stats = await new IndexingApi(session.Management(session.Endpoint(parse.GetValue(statEnv)))).StatisticsAsync(token);
            session.Output.Document("index statistic", w =>
            {
                w.WriteStartArray("indexes");
                foreach (var s in stats)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    w.WriteNumber("documents", s.Documents);
                    w.WriteNumber("fields", s.Fields);
                    w.WriteNumber("terms", s.Terms);
                    w.WriteBoolean("hasDeletions", s.HasDeletions);
                    w.WriteString("lastUpdated", s.LastUpdated);
                    w.WriteString("rebuildTime", s.RebuildTime);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            foreach (var s in stats)
            {
                session.Output.Line($"{s.Name}: {s.Documents} documents, {s.Fields} fields, {s.Terms} terms, last updated {s.LastUpdated}, rebuild time {s.RebuildTime}");
            }
            return ExitCodes.Success;
        }, ct));
        index.Subcommands.Add(stat);

        index.Subcommands.Add(IndexJob("rebuild", "Rebuild search indexes", (api, names, ct) => api.RebuildAsync(names, ct)));
        index.Subcommands.Add(IndexJob("schema-populate", "Populate the managed schema of search indexes", (api, names, ct) => api.PopulateSchemaAsync(names, ct)));
        return index;
    }

    public static Command BuildDbCleanup()
    {
        var environment = CommonOptions.EnvironmentName();
        var db = new Option<string[]>("--db", "--databases") { Description = "Databases to clean", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => ["core", "master", "web"] };
        var tasks = new (string Flag, string Alias, string Task, string Description)[]
        {
            ("--cb", "--clean-blobs", "clean-blobs", "Clean up blobs"),
            ("--cf", "--clean-fields", "clean-fields", "Clean up fields"),
            ("--cof", "--clean-orphan-fields", "clean-orphanfields", "Clean up orphan fields"),
            ("--cild", "--clean-invalid-language-data", "clean-invalidlanguagedata", "Clean up invalid language data"),
            ("--ccd", "--clean-cyclic-dependencies", "clean-cyclicdependencies", "Clean up cyclic dependencies"),
            ("--coi", "--clean-orphan-items", "clean-orphanitems", "Clean up orphan items"),
            ("--rd", "--rebuild-descendants", "rebuild-descendants", "Rebuild the descendants table"),
        };
        var cmd = new Command("dbcleanup", "Run database cleanup tasks") { environment, db };
        // The Official CLI's long names are the task names (--clean-orphanfields); the hyphenated
        // spellings stay as aliases.
        var options = tasks.Select(t => (Option: new Option<bool>(t.Flag, new[] { "--" + t.Task, t.Alias }.Distinct().ToArray()) { Description = t.Description }, t.Task)).ToList();
        options.ForEach(o => cmd.Options.Add(o.Option));
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var selected = options.Where(o => parse.GetValue(o.Option)).Select(o => o.Task).ToList();
            if (selected.Count == 0)
            {
                selected = tasks.Select(t => t.Task).ToList();
            }
            var api = new DatabaseApi(session.Management(session.Endpoint(parse.GetValue(environment))));
            var jobs = await api.CleanupAsync(Split(parse.GetValue(db)!), selected, token);
            var finished = await PollAsync(session.Output, jobs, ids => api.StatusAsync(ids, token), token);
            Report(session.Output, "dbcleanup", finished);
            return finished.Any(j => j.IsFailed) ? ExitCodes.Failure : ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static Command IndexJob(string name, string description, Func<IndexingApi, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<JobStatus>>> start)
    {
        var environment = CommonOptions.EnvironmentName();
        var indexes = new Option<string[]>("--indexes", "-i") { Description = "Index names; all indexes when omitted", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var cmd = new Command(name, description) { environment, indexes };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = new IndexingApi(session.Management(session.Endpoint(parse.GetValue(environment))));
            var jobs = await start(api, Split(parse.GetValue(indexes)!), token);
            var finished = await PollAsync(session.Output, jobs, ids => api.StatusAsync(ids, token), token);
            Report(session.Output, "index " + name, finished);
            return finished.Any(j => j.IsFailed) ? ExitCodes.Failure : ExitCodes.Success;
        }, ct));
        return cmd;
    }

    /// <summary>Starts a publish and polls it every 750 ms until it finishes, as the Official CLI does.</summary>
    public static async Task<JobStatus> RunAndWaitAsync(PublishingApi api, Output output, Func<Task<JobStatus>> start, CancellationToken ct)
    {
        var job = await start();
        output.Info($"Publishing job {job.Id} started.");
        while (!job.IsFinished)
        {
            await Task.Delay(PollInterval, ct);
            job = await api.StatusAsync(job.Id, ct);
            output.Debug($"  {job.StateName}: {job.ProcessedCount} items processed");
        }
        output.Info($"Publishing job {job.Id} {job.StateName.ToLowerInvariant()}: {job.ProcessedCount} items processed.");
        return job;
    }

    private static async Task<IReadOnlyList<JobStatus>> PollAsync(Output output, IReadOnlyList<JobStatus> jobs, Func<IReadOnlyList<string>, Task<IReadOnlyList<JobStatus>>> status, CancellationToken ct)
    {
        foreach (var j in jobs)
        {
            output.Info($"Job {j.Id} ({j.Name}) started.");
        }
        var current = jobs;
        while (current.Any(j => !j.IsFinished))
        {
            await Task.Delay(PollInterval, ct);
            current = await status(current.Select(j => j.Id).ToList());
        }
        return current;
    }

    private static void Report(Output output, string command, IEnumerable<JobStatus> jobs)
    {
        var list = jobs.ToList();
        output.Document(command, w =>
        {
            w.WriteStartArray("jobs");
            foreach (var j in list)
            {
                w.WriteStartObject();
                w.WriteString("id", j.Id);
                w.WriteString("name", j.Name);
                w.WriteString("state", j.StateName);
                w.WriteNumber("processed", j.ProcessedCount);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        foreach (var j in list)
        {
            output.Line($"{j.Name ?? j.Id}: {j.StateName} ({j.ProcessedCount} processed)");
        }
    }

    private static List<string> Split(string[] values) =>
        values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    private static List<string?> Targets(string[] values)
    {
        var list = Split(values);
        return list.Count == 0 ? [null] : list.Cast<string?>().ToList();
    }
}
