using System.CommandLine;
using System.Text;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Remote.Cloud;

namespace Tern.Cli.Commands;

public static partial class CloudCommands
{
    private static Command Environment()
    {
        var listProject = EnvironmentProjectId();
        var list = new Command("list", "List a project's environments") { listProject };
        Action(list, async (parse, session, cloud, ct) =>
        {
            var environments = CloudJson.Items(await cloud.Deploy.EnvironmentsAsync(parse.GetValue(listProject)!, ct)).ToList();
            session.Output.Document("cloud environment list", w => CloudOutput.WriteArray(w, "environments", environments, CloudOutput.Environment));
            session.Output.Line($"Found {environments.Count} environment(s)");
            foreach (var e in environments)
            {
                PrintObject(session.Output, $"{CloudJson.String(e, "name")} Environment detailed information:", e, CloudOutput.Environment);
                session.Output.Line();
            }
            return ExitCodes.Success;
        });

        var createProject = EnvironmentProjectId();
        var createName = EnvironmentName();
        var prod = new Option<bool>("--prod", "EnvironmentProduction") { Description = "Provision it as a production environment" };
        var cmOnly = new Option<bool>("--cm-only", "CmOnly") { Description = "Provision a CM only, without an editing host" };
        var create = new Command("create", "Create an environment in a project") { createProject, createName, prod, cmOnly };
        Action(create, async (parse, session, cloud, ct) =>
        {
            var created = await cloud.Deploy.CreateEnvironmentAsync(parse.GetValue(createProject)!, parse.GetValue(createName)!, parse.GetValue(prod), parse.GetValue(cmOnly) ? "cm" : null, null, ct);
            PrintCreatedEnvironment(session.Output, "cloud environment create", created, $"tern cloud deployment create --environment-id {CloudJson.String(created, "id")}");
            return ExitCodes.Success;
        });

        var updateId = EnvironmentId();
        var updateName = EnvironmentName();
        var update = new Command("update", "Rename an environment") { updateId, updateName };
        Action(update, async (parse, session, cloud, ct) =>
        {
            await cloud.Deploy.UpdateEnvironmentAsync(parse.GetValue(updateId)!, parse.GetValue(updateName)!, ct);
            Done(session.Output, "cloud environment update", parse.GetValue(updateId)!, "updated");
            return ExitCodes.Success;
        });

        var deleteId = EnvironmentId();
        var deleteForce = Force();
        var delete = new Command("delete", "Delete an environment") { deleteId, deleteForce };
        Action(delete, async (parse, session, cloud, ct) =>
        {
            Confirm(parse.GetValue(deleteForce), $"delete environment '{parse.GetValue(deleteId)}'");
            await cloud.Deploy.DeleteEnvironmentAsync(parse.GetValue(deleteId)!, ct);
            Done(session.Output, "cloud environment delete", parse.GetValue(deleteId)!, "deleted");
            return ExitCodes.Success;
        });

        var infoId = EnvironmentId();
        var info = new Command("info", "Show an environment") { infoId };
        Action(info, async (parse, session, cloud, ct) =>
        {
            var environment = await cloud.Deploy.EnvironmentAsync(parse.GetValue(infoId)!, ct);
            session.Output.Document("cloud environment info", w =>
            {
                w.WritePropertyName("environment");
                CloudOutput.WriteObject(w, environment, CloudOutput.Environment);
            });
            PrintObject(session.Output, $"{CloudJson.String(environment, "name")} Environment detailed information:", environment, CloudOutput.Environment);
            return ExitCodes.Success;
        });

        var promoteId = EnvironmentId();
        var sourceId = new Option<string>("--source-id") { Description = "The deployment to promote", Required = true };
        var promoteNoWatch = NoWatch();
        var promoteNoStart = NoStart();
        var promoteWait = WaitForPostActions();
        var promote = new Command("promote", "Promote a deployment to another environment") { promoteId, sourceId, promoteNoWatch, promoteNoStart, promoteWait };
        Action(promote, async (parse, session, cloud, ct) =>
        {
            var environmentId = parse.GetValue(promoteId)!;
            session.Output.Info($"Promoting deployment '{parse.GetValue(sourceId)}' to environment '{environmentId}'...");
            var deploymentId = await cloud.Deploy.PromoteAsync(environmentId, parse.GetValue(sourceId)!, ct);
            session.Output.Line($"The promotion created deployment '{deploymentId}'.");
            return await AfterCreateAsync(session, cloud, "cloud environment promote", deploymentId, environmentId, parse.GetValue(promoteNoStart), parse.GetValue(promoteNoWatch), parse.GetValue(promoteWait), null, ct);
        });

        var healthId = EnvironmentId();
        var health = new Command("health", "Show the health of an environment's CM") { healthId };
        Action(health, async (parse, session, cloud, ct) =>
        {
            var environment = await cloud.Deploy.EnvironmentAsync(parse.GetValue(healthId)!, ct);
            var host = CloudJson.String(environment, "host") ?? throw new CliException("SitecoreAI did not return the environment's host.");
            var status = await cloud.Deploy.CmHealthAsync(host, ct);
            session.Output.Document("cloud environment health", w =>
            {
                w.WriteString("environmentId", parse.GetValue(healthId));
                w.WriteString("host", host);
                w.WriteString("health", status);
            });
            session.Output.Line($"Environment health status: {status}");
            return ExitCodes.Success;
        });

        var restartId = EnvironmentId();
        var restart = new Command("restart", "Restart an environment's CM and wait for it") { restartId };
        Action(restart, async (parse, session, cloud, ct) =>
        {
            var id = parse.GetValue(restartId)!;
            session.Output.Info($"Restarting environment '{id}'...");
            await cloud.Deploy.RestartEnvironmentAsync(id, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(300));
            JsonElement status = default;
            var finished = false;
            try
            {
                while (true)
                {
                    status = await cloud.Deploy.RestartStatusAsync(id, timeout.Token);
                    if (status.ValueKind == JsonValueKind.Object && CloudOutput.Status(status, "restartStatus") != 1)
                    {
                        finished = true;
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            session.Output.Document("cloud environment restart", w =>
            {
                w.WritePropertyName("restart");
                CloudOutput.WriteObject(w, status.ValueKind == JsonValueKind.Object ? status : EmptyObject, CloudOutput.RestartStatus);
            });
            var complete = status.ValueKind == JsonValueKind.Object && CloudOutput.Status(status, "restartStatus") == 2;
            if (complete)
            {
                session.Output.Line($"Environment '{id}' restarted");
            }
            else
            {
                session.Output.Warn(finished
                    ? $"The restart of environment '{id}' ended as {CloudOutput.TaskStatuses[CloudOutput.Status(status, "restartStatus")]}."
                    : $"Environment '{id}' was still restarting after 5 minutes.");
            }
            return ExitCodes.Success;
        });

        return Group("environment", "Manage a project's SitecoreAI environments",
            list, create, update, delete, info, Connect(), Disconnect(), promote, health, restart, Variable(), Log());
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static void Done(Output output, string command, string environmentId, string verb)
    {
        output.Document(command, w => w.WriteString("environmentId", environmentId));
        output.Line($"Environment Id '{environmentId}' has been {verb}");
    }

    private static void PrintCreatedEnvironment(Output output, string command, JsonElement created, string hint)
    {
        output.Document(command, w =>
        {
            w.WritePropertyName("environment");
            CloudOutput.WriteObject(w, created, CloudOutput.CreatedEnvironment);
        });
        output.Line("Environment created.");
        output.Line($"'{CloudJson.String(created, "name")}' Environment was created within '{CloudJson.String(created, "projectName")}' Project for the '{CloudJson.String(created, "organizationName")}' Organization.");
        CloudOutput.Table(created, CloudOutput.CreatedEnvironment).ToList().ForEach(output.Line);
        output.Line();
        output.Line("Add a deployment to it with:");
        output.Line(hint);
    }

    private static Command Connect()
    {
        var allowWrite = AllowWrite();
        var audience = Audience();
        var authority = Authority();
        var clientCredentials = ClientCredentials();
        var clientId = ClientId();
        var clientSecret = ClientSecret();
        var environmentId = EnvironmentId();
        var cmHost = new Option<string?>("--cm-host", "-host") { Description = "The CM's URL or host name (default: the environment's host)" };
        var cmd = new Command("connect", "Add an endpoint for an environment's CM to user.json, using the SitecoreAI login") { allowWrite, audience, authority, clientCredentials, clientId, clientSecret, environmentId, cmHost };
        Action(cmd, async (parse, session, cloud, ct) =>
        {
            var useClientCredentials = parse.GetValue(clientCredentials);
            if (useClientCredentials && string.IsNullOrWhiteSpace(parse.GetValue(clientId)) && string.IsNullOrWhiteSpace(parse.GetValue(clientSecret)))
            {
                throw new CliException("--client-credentials needs --client-id and --client-secret.");
            }
            if (!useClientCredentials && new[] { parse.GetValue(audience), parse.GetValue(authority), parse.GetValue(clientId), parse.GetValue(clientSecret) }.Any(v => !string.IsNullOrWhiteSpace(v)))
            {
                throw new CliException("Client credentials were given without --client-credentials; they are only used with it.");
            }
            var host = parse.GetValue(cmHost);
            if (host is not null && Uri.TryCreate(host, UriKind.Absolute, out var parsed) && string.IsNullOrEmpty(parsed.Host))
            {
                throw new CliException("--cm-host must be the CM's host name or full URL.");
            }
            session.Output.Info("Connecting to the environment...");
            var environment = await cloud.Deploy.EnvironmentAsync(parse.GetValue(environmentId)!, ct);
            var name = CloudJson.String(environment, "name") ?? throw new CliException("SitecoreAI did not return the environment's name.");
            var url = CloudEndpoints.CmHostUrl(host, CloudJson.String(environment, "host") ?? string.Empty);
            var endpoint = CloudEndpoints.Connect(session.Store, name, url, parse.GetValue(allowWrite));
            if (useClientCredentials)
            {
                endpoint.SetOwn("authority", parse.GetValue(authority) ?? Tern.Remote.Auth.Defaults.Authority);
                endpoint.SetOwn("clientId", parse.GetValue(clientId));
                endpoint.SetOwn("clientSecret", parse.GetValue(clientSecret));
                endpoint.SetOwn("audience", parse.GetValue(audience) ?? Tern.Remote.Auth.Defaults.Audience);
            }
            session.Store.Save();
            session.Output.Document("cloud environment connect", w =>
            {
                w.WriteString("endpoint", endpoint.Name);
                w.WriteString("host", endpoint.Host);
                w.WriteString("ref", endpoint.Ref);
                w.WriteBoolean("allowWrite", endpoint.AllowWrite);
            });
            session.Output.Line($"Endpoint '{endpoint.Name}' now points at {endpoint.Host} using the {CloudEndpoints.Name} login{(endpoint.AllowWrite ? string.Empty : " (read-only)")}.");
            return ExitCodes.Success;
        });
        return cmd;
    }

    private static Command Disconnect()
    {
        var name = new Option<string?>("--name", "-n", "EnvironmentName") { Description = "The environment's name (the endpoint to remove)" };
        var environmentId = new Option<string?>("--environment-id", "-id") { Description = "The environment's ID" };
        var cmd = new Command("disconnect", "Remove an environment's endpoint from user.json") { name, environmentId };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var endpointName = parse.GetValue(name);
            var id = parse.GetValue(environmentId);
            if (string.IsNullOrEmpty(endpointName) && string.IsNullOrEmpty(id))
            {
                throw new CliException("Pass --name or --environment-id.");
            }
            if (!string.IsNullOrEmpty(id))
            {
                var cloud = CloudContext.Open(session);
                var actual = CloudJson.String(await cloud.Deploy.EnvironmentAsync(id, token), "name");
                if (string.IsNullOrEmpty(endpointName))
                {
                    endpointName = actual;
                }
                else if (!endpointName.Equals(actual, StringComparison.OrdinalIgnoreCase))
                {
                    session.Output.Line($"Environment name {endpointName} doesn't match the id {id}.");
                    return ExitCodes.Success;
                }
            }
            session.Output.Info("Disconnecting from the environment...");
            var endpoint = session.Store.Find(endpointName);
            if (endpoint is null)
            {
                session.Output.Line($"Endpoint {endpointName} is not present.");
                return ExitCodes.Success;
            }
            session.Store.Remove(endpoint.Name);
            session.Store.Save();
            session.Output.Document("cloud environment disconnect", w => w.WriteString("removed", endpoint.Name));
            session.Output.Line($"Successfully disconnected from the environment {endpoint.Name}");
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static Command Variable()
    {
        var deleteId = EnvironmentId();
        var deleteName = VariableName();
        var delete = new Command("delete", "Delete an environment variable") { deleteId, deleteName };
        Action(delete, async (parse, session, cloud, ct) =>
        {
            await cloud.Deploy.DeleteVariableAsync(parse.GetValue(deleteId)!, parse.GetValue(deleteName)!, ct);
            session.Output.Document("cloud environment variable delete", w =>
            {
                w.WriteString("environmentId", parse.GetValue(deleteId));
                w.WriteString("name", parse.GetValue(deleteName));
            });
            session.Output.Line($"'{parse.GetValue(deleteName)}' Environment variable was deleted within '{parse.GetValue(deleteId)}' Environment.");
            return ExitCodes.Success;
        });

        var listId = EnvironmentId();
        var list = new Command("list", "List an environment's variables") { listId };
        Action(list, async (parse, session, cloud, ct) =>
        {
            var variables = CloudJson.Items(await cloud.Deploy.VariablesAsync(parse.GetValue(listId)!, ct)).ToList();
            // The Official CLI shows a variable without a target as applying to every target.
            IReadOnlyDictionary<string, string>? AnyTarget(JsonElement v) => CloudJson.String(v, "target") is null ? new Dictionary<string, string> { ["target"] = "*" } : null;
            session.Output.Document("cloud environment variable list", w => CloudOutput.WriteArray(w, "variables", variables, CloudOutput.Variable, AnyTarget));
            session.Output.Line($"Found {variables.Count} variable(s)");
            foreach (var v in variables)
            {
                PrintObject(session.Output, $"{CloudJson.String(v, "name")} Environment variable detailed information:", v, CloudOutput.Variable, AnyTarget(v));
                session.Output.Line();
            }
            return ExitCodes.Success;
        });

        var upsertId = EnvironmentId();
        var upsertName = VariableName();
        var value = new Option<string>("--value", "-val", "EnvironmentVariableValue") { Description = "The variable's value", Required = true };
        var target = new Option<string?>("--target", "EnvironmentVariableTarget") { Description = "Where the variable applies: CM or a rendering host's name (default: everywhere)" };
        var secret = new Option<bool>("--secret", "-s", "EnvironmentVariableSecret") { Description = "Store the value encrypted" };
        var upsert = new Command("upsert", "Create or update an environment variable") { upsertId, upsertName, value, target, secret };
        Action(upsert, async (parse, session, cloud, ct) =>
        {
            await cloud.Deploy.UpsertVariableAsync(parse.GetValue(upsertId)!, parse.GetValue(upsertName)!, parse.GetValue(value)!, parse.GetValue(secret), parse.GetValue(target), ct);
            session.Output.Document("cloud environment variable upsert", w =>
            {
                w.WriteString("environmentId", parse.GetValue(upsertId));
                w.WriteString("name", parse.GetValue(upsertName));
                w.WriteBoolean("secret", parse.GetValue(secret));
                w.WriteString("target", parse.GetValue(target));
            });
            session.Output.Line($"'{parse.GetValue(upsertName)}' Environment variable was accepted for upsert within '{parse.GetValue(upsertId)}' Environment.");
            return ExitCodes.Success;
        });

        return Group("variable", "Manage an environment's variables", delete, list, upsert);
    }

    private static Option<string> VariableName() =>
        new("--name", "-n", "EnvironmentVariableName") { Description = "The variable's name, unique within the environment", Required = true };

    private static Command Log()
    {
        var listId = EnvironmentId();
        var latest = new Option<bool>("--latest") { Description = "Only the latest file of each type" };
        var list = new Command("list", "List the CM's log files") { listId, latest };
        Action(list, async (parse, session, cloud, ct) =>
        {
            var files = CloudJson.Items(await cloud.Monitoring.LogFilesAsync(parse.GetValue(listId)!, parse.GetValue(latest), ct)).ToList();
            session.Output.Document("cloud environment log list", w => CloudOutput.WriteArray(w, "logFiles", files, CloudOutput.LogFile));
            session.Output.Line($"Listing filenames of {files.Count} log file(s):");
            session.Output.Line();
            foreach (var f in files)
            {
                session.Output.Line($"- {CloudJson.String(f, "name")}");
            }
            return ExitCodes.Success;
        });

        var downloadId = EnvironmentId();
        var downloadFile = LogFile();
        var outputPath = OutputPath();
        var timeout = Timeout();
        var download = new Command("download", "Download a CM log file") { downloadId, downloadFile, outputPath, timeout };
        Action(download, async (parse, session, cloud, ct) =>
        {
            var file = parse.GetValue(downloadFile)!;
            var seconds = Seconds(parse.GetValue(timeout));
            var bytes = await cloud.Monitoring.LogFileAsync(parse.GetValue(downloadId)!, file, download: true, seconds is { } s ? TimeSpan.FromSeconds(s) : null, ct);
            if (bytes.Length == 0)
            {
                session.Output.Line($"Log file {file} of environment {parse.GetValue(downloadId)} is empty.");
                session.Output.Document("cloud environment log download", w => w.WriteNull("path"));
                return ExitCodes.Success;
            }
            var path = SaveFile(parse.GetValue(outputPath), file, bytes);
            session.Output.Document("cloud environment log download", w =>
            {
                w.WriteString("path", path);
                w.WriteNumber("bytes", bytes.Length);
            });
            session.Output.Line($"Log File {file} saved at location: {path}");
            return ExitCodes.Success;
        });

        var viewId = EnvironmentId();
        var viewFile = LogFile();
        var view = new Command("view", "Print a CM log file") { viewId, viewFile };
        Action(view, async (parse, session, cloud, ct) =>
        {
            var file = parse.GetValue(viewFile)!;
            var bytes = await cloud.Monitoring.LogFileAsync(parse.GetValue(viewId)!, file, download: false, null, ct);
            var text = Encoding.UTF8.GetString(bytes);
            session.Output.Document("cloud environment log view", w =>
            {
                w.WriteString("file", file);
                w.WriteString("content", text);
            });
            if (bytes.Length == 0)
            {
                session.Output.Line($"Log file {file} of environment {parse.GetValue(viewId)} is empty.");
                return ExitCodes.Success;
            }
            session.Output.Info("File content starting in the lines below");
            if (!session.Output.Json)
            {
                session.Output.Out.Write(text);
                if (!text.EndsWith('\n'))
                {
                    session.Output.Out.WriteLine();
                }
            }
            session.Output.Info("File content ended");
            return ExitCodes.Success;
        });

        return Group("log", "Read the CM's log files", list, download, view);
    }

    private static Option<string> LogFile() => new("--logfile", "--log") { Description = "The log file's name", Required = true };
    private static Option<string?> OutputPath() => new("--outputpath", "--path", "-o") { Description = "The folder (or file) to save to (default: the current folder)" };

    /// <summary>
    /// Saves a download as the Official CLI does: a path with an extension is the file itself;
    /// any other path is the folder to save <paramref name="fileName"/> in.
    /// </summary>
    private static string SaveFile(string? outputPath, string fileName, byte[] bytes)
    {
        var target = string.IsNullOrWhiteSpace(outputPath) ? "." : outputPath;
        var full = Path.HasExtension(target) ? Path.GetFullPath(target) : Path.Combine(Path.GetFullPath(target), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    // editinghost

    private static Command EditingHost()
    {
        var createName = EnvironmentName();
        var cmEnvironmentId = new Option<string>("--cm-environment-id", "-cm") { Description = "The ID of the CM environment the editing host serves", Required = true };
        var create = new Command("create", "Create an editing host environment for a CM environment") { createName, cmEnvironmentId };
        Action(create, async (parse, session, cloud, ct) =>
        {
            var cm = await cloud.Deploy.EnvironmentAsync(parse.GetValue(cmEnvironmentId)!, ct);
            var projectId = CloudJson.String(cm, "projectId") ?? throw new CliException("SitecoreAI did not return the CM environment's project.");
            // The editing host takes the CM's tenant type.
            var production = CloudJson.String(cm, "tenantType") == "prod";
            var created = await cloud.Deploy.CreateEnvironmentAsync(projectId, parse.GetValue(createName)!, production, "eh", parse.GetValue(cmEnvironmentId), ct);
            PrintCreatedEnvironment(session.Output, "cloud editinghost create", created, $"tern cloud editinghost deploy --environment-id {CloudJson.String(created, "id")}");
            return ExitCodes.Success;
        });

        var updateId = EnvironmentId();
        var updateName = EnvironmentName();
        var update = new Command("update", "Rename an editing host environment") { updateId, updateName };
        Action(update, async (parse, session, cloud, ct) =>
        {
            await RequireEditingHostAsync(cloud, parse.GetValue(updateId)!, ct);
            await cloud.Deploy.UpdateEnvironmentAsync(parse.GetValue(updateId)!, parse.GetValue(updateName)!, ct);
            Done(session.Output, "cloud editinghost update", parse.GetValue(updateId)!, "updated");
            return ExitCodes.Success;
        });

        var deleteId = EnvironmentId();
        var deleteForce = Force();
        var delete = new Command("delete", "Delete an editing host environment") { deleteId, deleteForce };
        Action(delete, async (parse, session, cloud, ct) =>
        {
            Confirm(parse.GetValue(deleteForce), $"delete editing host environment '{parse.GetValue(deleteId)}'");
            await RequireEditingHostAsync(cloud, parse.GetValue(deleteId)!, ct);
            await cloud.Deploy.DeleteEnvironmentAsync(parse.GetValue(deleteId)!, ct);
            Done(session.Output, "cloud editinghost delete", parse.GetValue(deleteId)!, "deleted");
            return ExitCodes.Success;
        });

        var deployId = EnvironmentId();
        var upload = Upload();
        var workingDir = WorkingDir();
        var noWatch = NoWatch();
        var noStart = NoStart();
        var timeout = Timeout();
        var deploy = new Command("deploy", "Create (and by default start) a deployment of an editing host environment") { deployId, upload, workingDir, noWatch, noStart, timeout };
        Action(deploy, async (parse, session, cloud, ct) =>
        {
            await RequireEditingHostAsync(cloud, parse.GetValue(deployId)!, ct);
            return await CreateDeploymentAsync(session, cloud, "cloud editinghost deploy", parse.GetValue(deployId)!, parse.GetValue(upload), parse.GetValue(workingDir),
                parse.GetValue(noStart), parse.GetValue(noWatch), waitForPostActions: false, Seconds(parse.GetValue(timeout)), dnsTimeout: null, ct);
        });

        return Group("editinghost", "Manage SitecoreAI editing host environments", create, update, delete, deploy);
    }

    private static async Task RequireEditingHostAsync(CloudContext cloud, string environmentId, CancellationToken ct)
    {
        if (CloudJson.String(await cloud.Deploy.EnvironmentAsync(environmentId, ct), "type") != "eh")
        {
            throw new CliException($"Environment '{environmentId}' is not an editing host environment.");
        }
    }
}
