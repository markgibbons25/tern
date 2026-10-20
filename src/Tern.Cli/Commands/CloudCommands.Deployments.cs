using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Remote.Cloud;

namespace Tern.Cli.Commands;

public static partial class CloudCommands
{
    private const int NotStarted = 0, InProgress = 1, Complete = 2, Failed = 3, Skipped = 4;

    /// <summary>How long a deployment is followed before giving up, and how often it is polled, as in the Official CLI.</summary>
    internal static TimeSpan WatchTimeout { get; set; } = TimeSpan.FromSeconds(1800);
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDnsTimeout = TimeSpan.FromSeconds(300);

    private static Command Deployment()
    {
        var infoId = DeploymentId();
        var info = new Command("info", "Show a deployment") { infoId };
        Action(info, async (parse, session, cloud, ct) =>
        {
            var deployment = await cloud.Deploy.DeploymentAsync(parse.GetValue(infoId)!, ct);
            session.Output.RawJson(w => CloudOutput.WriteObject(w, deployment, CloudOutput.Deployment));
            var id = CloudJson.String(deployment, "id") ?? parse.GetValue(infoId)!;
            var page = CloudEndpoints.DeploymentPage(id);
            switch (CloudOutput.Status(deployment, "calculatedStatus"))
            {
                case InProgress:
                    session.Output.Line("This deployment is currently in progress.");
                    session.Output.Line();
                    session.Output.Line("To see real-time updates go to: ");
                    break;
                case Complete:
                    session.Output.Line("This deployment has successfully completed.");
                    session.Output.Line();
                    session.Output.Line("To see detailed deployment log information for this deployment go to:");
                    break;
                case Failed:
                    session.Output.Line("This deployment attempt has failed.");
                    session.Output.Line();
                    session.Output.Line("To see detailed deployment log information for this deployment go to:");
                    break;
                case Skipped:
                    session.Output.Line("This deployment attempt has skipped.");
                    session.Output.Line();
                    session.Output.Line("To see detailed deployment log information for this deployment go to:");
                    break;
                default:
                    page = null;
                    break;
            }
            if (page is not null)
            {
                session.Output.Line(page);
                session.Output.Line();
            }
            PrintObject(session.Output, $"Deployment Id {id} detailed information:", deployment, CloudOutput.Deployment);
            return ExitCodes.Success;
        });

        var listId = EnvironmentId();
        var list = new Command("list", "List an environment's deployments") { listId };
        Action(list, async (parse, session, cloud, ct) =>
        {
            var deployments = CloudJson.Items(await cloud.Deploy.DeploymentsAsync(parse.GetValue(listId)!, ct)).ToList();
            session.Output.RawJson(w => CloudOutput.WriteArray(w, deployments, CloudOutput.Deployment));
            session.Output.Line($"Found {deployments.Count} deployment(s)");
            foreach (var d in deployments)
            {
                session.Output.Line();
                PrintObject(session.Output, $"Deployment Id {CloudJson.String(d, "id")} detailed information:", d, CloudOutput.Deployment);
            }
            return ExitCodes.Success;
        });

        var createId = EnvironmentId();
        var upload = Upload();
        var workingDir = WorkingDir();
        var createNoWatch = NoWatch();
        var createNoStart = NoStart();
        var timeout = Timeout();
        var createWait = WaitForPostActions();
        var createDns = DnsTimeout();
        var create = new Command("create", "Create (and by default start and follow) a deployment of an environment") { createId, upload, workingDir, createNoWatch, createNoStart, timeout, createWait, createDns };
        Action(create, async (parse, session, cloud, ct) =>
        {
            var environmentId = parse.GetValue(createId)!;
            if (!string.IsNullOrEmpty(parse.GetValue(workingDir)) && !parse.GetValue(upload))
            {
                throw new CliException("--working-dir needs --upload.");
            }
            if (CloudJson.String(await cloud.Deploy.EnvironmentAsync(environmentId, ct), "type") == "eh")
            {
                throw new CliException($"Environment '{environmentId}' is an editing host; deploy it with `tern cloud editinghost deploy`.");
            }
            return await CreateDeploymentAsync(session, cloud, "cloud deployment create", environmentId, parse.GetValue(upload), parse.GetValue(workingDir),
                parse.GetValue(createNoStart), parse.GetValue(createNoWatch), parse.GetValue(createWait), Seconds(parse.GetValue(timeout)), parse.GetValue(createDns), ct);
        });

        var startId = DeploymentId();
        var startNoWatch = NoWatch();
        var startWait = WaitForPostActions();
        var startDns = DnsTimeout();
        var start = new Command("start", "Start a deployment that was created with --no-start") { startId, startNoWatch, startWait, startDns };
        Action(start, (parse, session, cloud, ct) =>
            StartAsync(session, cloud, "cloud deployment start", parse.GetValue(startId)!, parse.GetValue(startNoWatch), parse.GetValue(startWait), parse.GetValue(startDns), ct));

        var watchId = DeploymentId();
        var watchWait = WaitForPostActions();
        var watchDns = DnsTimeout();
        var watch = new Command("watch", "Follow a deployment until it finishes") { watchId, watchWait, watchDns };
        Action(watch, async (parse, session, cloud, ct) =>
        {
            var result = await WatchAsync(session.Output, cloud, parse.GetValue(watchId)!, parse.GetValue(watchWait), parse.GetValue(watchDns), ct);
            session.Output.RawJson(w => WriteWatchResult(w, result));
            return result.ExitCode;
        });

        var cancelId = DeploymentId();
        var cancel = new Command("cancel", "Cancel a deployment in progress") { cancelId };
        Action(cancel, async (parse, session, cloud, ct) =>
        {
            session.Output.Info("Canceling deployment...");
            await cloud.Deploy.CancelDeploymentAsync(parse.GetValue(cancelId)!, ct);
            session.Output.RawJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("deploymentId", parse.GetValue(cancelId));
                w.WriteEndObject();
            });
            session.Output.Line($"Deployment {parse.GetValue(cancelId)} canceled.");
            return ExitCodes.Success;
        });

        var logId = DeploymentId();
        var logPath = OutputPath();
        var log = new Command("log", "Show a deployment's build and deploy logs") { logId, logPath };
        Action(log, async (parse, session, cloud, ct) =>
        {
            var id = parse.GetValue(logId)!;
            var deploymentTask = cloud.Deploy.DeploymentAsync(id, ct);
            var logsTask = cloud.Monitoring.DeploymentLogsAsync(id, ct);
            await Task.WhenAll(deploymentTask, logsTask);
            var stages = StageStatuses(deploymentTask.Result);
            var lines = CloudJson.TryGet(logsTask.Result, "logs", out var logs) ? CloudJson.Items(logs).ToList() : [];
            void WriteLogs(Utf8JsonWriter w)
            {
                w.WriteStartArray("Stage");
                foreach (var (name, state) in stages)
                {
                    w.WriteStartObject();
                    w.WriteString("Name", name);
                    w.WriteString("State", state);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("Logs");
                CloudOutput.WriteArray(w, lines, CloudOutput.DeploymentLogLine);
            }
            var outputPath = parse.GetValue(logPath);
            if (session.Output.Json)
            {
                session.Output.RawJson(w =>
                {
                    w.WriteStartObject();
                    WriteLogs(w);
                    w.WriteEndObject();
                });
                return ExitCodes.Success;
            }
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var path = SaveFile(outputPath, $"Deployment_{id}_logs.json", CloudJson.Write(WriteLogs, indented: true));
                session.Output.Line($"Log File Deployment_{id}_logs.json saved at location: {path}");
                return ExitCodes.Success;
            }
            foreach (var group in lines.GroupBy(l => CloudJson.String(l, "stage") ?? string.Empty))
            {
                session.Output.Line($"{group.Key} - {(stages.FirstOrDefault(s => s.Name == group.Key).State ?? "Status Unknown")}");
                foreach (var line in group)
                {
                    var time = CloudJson.String(line, "logTime") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToString(CultureInfo.CurrentCulture) : CloudJson.String(line, "logTime");
                    session.Output.Line($"{time}|{CloudJson.String(line, "logLevel")?.ToUpperInvariant()}|{CloudJson.String(line, "logMessage")}");
                }
                session.Output.Line();
            }
            return ExitCodes.Success;
        });

        return Group("deployment", "Manage an environment's SitecoreAI deployments", info, list, create, start, watch, cancel, log);
    }

    /// <summary>Each stage of a deployment and its status name, as the deployment log reports them.</summary>
    private static List<(string Name, string? State)> StageStatuses(JsonElement deployment)
    {
        var stages = new List<(string, string?)>
        {
            ("Provisioning", CloudOutput.TaskStatuses[CloudOutput.Status(deployment, "provisioningStatus")]),
            ("Build", CloudOutput.TaskStatuses[CloudOutput.Status(deployment, "buildStatus")]),
            ("Deployment", CloudOutput.TaskStatuses[CloudOutput.Status(deployment, "deploymentStatus")]),
        };
        if (CloudOutput.OptionalStatus(deployment, "postActionStatus") is { } post)
        {
            stages.Add(("PostAction", CloudOutput.TaskStatuses[post]));
        }
        stages.Add(("Calculated", CloudOutput.TaskStatuses[CloudOutput.Status(deployment, "calculatedStatus")]));
        return stages;
    }

    /// <summary>Creates a deployment, uploads the source when asked, and then starts it unless told not to.</summary>
    private static async Task<int> CreateDeploymentAsync(Session session, CloudContext cloud, string command, string environmentId, bool upload, string? workingDir,
        bool noStart, bool noWatch, bool waitForPostActions, int? timeoutSeconds, int? dnsTimeout, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(workingDir) && !upload)
        {
            throw new CliException("--working-dir needs --upload.");
        }
        SourceArchive.Result? archive = null;
        if (upload)
        {
            // Zip before creating the deployment, so a bad folder leaves nothing behind in SitecoreAI.
            var folder = string.IsNullOrEmpty(workingDir) ? session.Root.RootDirectory : Path.GetFullPath(workingDir);
            session.Output.Info($"Zipping {folder}, leaving out what .gitignore ignores...");
            archive = SourceArchive.Create(folder);
            session.Output.Info($"{archive.Files.Count} files, {archive.Zip.Length / 1024.0 / 1024.0:F1} MB.");
            foreach (var file in archive.Files)
            {
                session.Output.Debug(file);
            }
        }
        session.Output.Debug("Creating deployment...");
        var deploymentId = await cloud.Deploy.CreateDeploymentAsync(environmentId, ct);
        if (archive is not null)
        {
            session.Output.Info($"Uploading the source of deployment '{deploymentId}'...");
            await cloud.Deploy.UploadSourceAsync(deploymentId, archive.Zip, timeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null, ct);
        }
        return await AfterCreateAsync(session, cloud, command, deploymentId, environmentId, noStart, noWatch, waitForPostActions, dnsTimeout, ct);
    }

    private static async Task<int> AfterCreateAsync(Session session, CloudContext cloud, string command, string deploymentId, string environmentId,
        bool noStart, bool noWatch, bool waitForPostActions, int? dnsTimeout, CancellationToken ct)
    {
        session.Output.Line($"Deployment '{deploymentId}' has been created for the environment '{environmentId}'.");
        if (!noStart)
        {
            return await StartAsync(session, cloud, command, deploymentId, noWatch, waitForPostActions, dnsTimeout, ct);
        }
        session.Output.Line($"It was not started. Start it with `tern cloud deployment start --deployment-id {deploymentId}`.");
        var deployment = await cloud.Deploy.DeploymentAsync(deploymentId, ct);
        session.Output.RawJson(w => CloudOutput.WriteObject(w, deployment, CloudOutput.Deployment));
        return ExitCodes.Success;
    }

    private static async Task<int> StartAsync(Session session, CloudContext cloud, string command, string deploymentId, bool noWatch, bool waitForPostActions, int? dnsTimeout, CancellationToken ct)
    {
        await cloud.Deploy.StartDeploymentAsync(deploymentId, ct);
        var page = CloudEndpoints.DeploymentPage(deploymentId);
        if (noWatch)
        {
            var deployment = await cloud.Deploy.DeploymentAsync(deploymentId, ct);
            session.Output.RawJson(w => CloudOutput.WriteObject(w, deployment, CloudOutput.Deployment));
            session.Output.Line($"Deployment '{deploymentId}' has started. Run `tern cloud deployment info --deployment-id {deploymentId}` to see its details.");
            session.Output.Line($"To see realtime progress go to: {page}");
            return ExitCodes.Success;
        }
        session.Output.Line("Deployment has been started");
        session.Output.Line($"To see realtime progress go to: {page}");
        var result = await WatchAsync(session.Output, cloud, deploymentId, waitForPostActions, dnsTimeout, ct);
        session.Output.RawJson(w => WriteWatchResult(w, result));
        return result.ExitCode;
    }

    internal sealed record WatchResult(
        string DeploymentId,
        bool IsCompleted,
        bool IsTimedOut,
        int ExitCode,
        IReadOnlyList<(string Stage, string? Status, TimeSpan Duration, string? Error)> Stages,
        string? Instance);

    /// <summary>
    /// Follows a deployment until it finishes, fails or is skipped (and, with
    /// <paramref name="waitForPostActions"/>, until its post actions finish), then waits for the
    /// CM's host name to answer. Exits with 1 when the deployment failed, as the Official CLI does.
    /// </summary>
    private static async Task<WatchResult> WatchAsync(Output output, CloudContext cloud, string deploymentId, bool waitForPostActions, int? dnsTimeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(WatchTimeout);
        var timedOut = false;
        string? last = null;
        JsonElement deployment;
        while (true)
        {
            try
            {
                await Task.Delay(PollInterval, limit.Token);
                deployment = await cloud.Deploy.DeploymentAsync(deploymentId, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }
            var summary = string.Join("  ", Stages(deployment, waitForPostActions).Select(s => $"{s.Stage}: {s.Status}{(s.Duration >= TimeSpan.FromSeconds(1) ? $" ({Duration(s.Duration)})" : string.Empty)}"));
            if (summary != last)
            {
                output.Info(summary);
                last = summary;
            }
            if (IsFinished(deployment, waitForPostActions))
            {
                break;
            }
        }
        deployment = await cloud.Deploy.DeploymentAsync(deploymentId, ct);
        var status = CloudOutput.Status(deployment, "calculatedStatus");
        var stages = Stages(deployment, waitForPostActions);
        var page = CloudEndpoints.DeploymentPage(deploymentId);
        foreach (var stage in stages.Where(s => !string.IsNullOrEmpty(s.Error)))
        {
            output.Warn($"{stage.Stage}: {stage.Error}");
        }
        string? instance = null;
        var exitCode = ExitCodes.Success;
        switch (status)
        {
            case Failed:
                exitCode = ExitCodes.Failure;
                output.Error($"The deployment failed. Its logs are at {page}");
                break;
            case Skipped:
                output.Warn("The deployment was skipped.");
                break;
            case Complete:
                output.Line("Deploy completed");
                var environment = await cloud.Deploy.EnvironmentAsync(CloudJson.String(deployment, "environmentId") ?? string.Empty, ct);
                if (CloudJson.String(environment, "type") != "eh" && CloudJson.String(environment, "host") is { } host)
                {
                    var cm = DeployApi.CmUrl(host, "/");
                    instance = cm.ToString();
                    output.Info("Waiting for the CM's host name to resolve...");
                    var resolved = await cloud.Deploy.WaitForCmAsync(host, dnsTimeout is { } s ? TimeSpan.FromSeconds(s) : DefaultDnsTimeout, null, ct);
                    output.Line(resolved
                        ? $"Your deployment is complete and the Sitecore CM instance is available here: {cm}"
                        : $"Your deployment is complete, but the Sitecore CM's host name did not resolve in time. It will be available here: {cm}");
                }
                break;
            default:
                output.Warn(timedOut ? $"Stopped following the deployment after {WatchTimeout.TotalMinutes:F0} minutes; it is still running." : "The deployment has not finished.");
                output.Line($"To see realtime progress go to: {page}");
                break;
        }
        return new WatchResult(deploymentId, status == Complete, timedOut, exitCode, stages, instance);
    }

    private static bool IsFinished(JsonElement deployment, bool waitForPostActions) =>
        CloudOutput.Status(deployment, "calculatedStatus") switch
        {
            Failed or Skipped => true,
            Complete => !waitForPostActions || CloudOutput.OptionalStatus(deployment, "postActionStatus") is Complete or Failed,
            _ => false,
        };

    private static List<(string Stage, string? Status, TimeSpan Duration, string? Error)> Stages(JsonElement deployment, bool waitForPostActions)
    {
        var names = new List<(string Stage, string Prefix)> { ("Provisioning", "provisioning"), ("Build", "build"), ("Deployment", "deployment") };
        if (waitForPostActions)
        {
            names.Add(("PostAction", "postAction"));
        }
        var stages = names.Select(n =>
        {
            var status = CloudOutput.OptionalStatus(deployment, n.Prefix + "Status");
            return (n.Stage, status is { } s ? CloudOutput.TaskStatuses[s] : null, Span(deployment, n.Prefix + "StartedAt", n.Prefix + "CompletedAt"), CloudJson.String(deployment, n.Prefix + "LastFailureMessage"));
        }).ToList();
        stages.Add(("Calculated", CloudOutput.TaskStatuses[CloudOutput.Status(deployment, "calculatedStatus")], Span(deployment, "startedAt", "completedAt"), null));
        return stages;
    }

    private static TimeSpan Span(JsonElement deployment, string started, string completed)
    {
        var now = DateTime.UtcNow;
        return (CloudOutput.Date(deployment, completed) ?? now) - (CloudOutput.Date(deployment, started) ?? now);
    }

    private static string Duration(TimeSpan span) =>
        span.Days != 0 ? $"{span.Days:D2}d:{span.Hours:D2}h:{span.Minutes:D2}m:{span.Seconds:D2}s"
        : span.Hours != 0 ? $"{span.Hours:D2}h:{span.Minutes:D2}m:{span.Seconds:D2}s"
        : $"{span.Minutes:D2}m:{span.Seconds:D2}s";

    /// <summary>The watch result in the Official CLI's JSON shape.</summary>
    private static void WriteWatchResult(Utf8JsonWriter w, WatchResult result)
    {
        w.WriteStartObject();
        w.WriteBoolean("IsTimedOut", result.IsTimedOut);
        w.WriteBoolean("IsCompleted", result.IsCompleted);
        w.WriteStartObject("StatusByTask");
        foreach (var s in result.Stages)
        {
            w.WriteString(s.Stage, s.Status);
        }
        w.WriteEndObject();
        w.WriteStartObject("DurationByTask");
        foreach (var s in result.Stages)
        {
            w.WriteString(s.Stage, s.Duration.ToString("c", CultureInfo.InvariantCulture));
        }
        w.WriteEndObject();
        w.WriteStartObject("ErrorByTask");
        foreach (var s in result.Stages)
        {
            w.WriteString(s.Stage, s.Error);
        }
        w.WriteEndObject();
        w.WriteNumber("ExitCode", result.ExitCode);
        w.WriteString("Instance", result.Instance ?? string.Empty);
        w.WriteString("DeploymentId", result.DeploymentId);
        w.WriteEndObject();
    }
}
