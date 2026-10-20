using System.CommandLine;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Remote.Auth;
using Tern.Remote.Cloud;
using Tern.Remote.Login;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>cloud</c>: SitecoreAI's projects, environments, editing hosts, deployments and organization,
/// through the Deploy API and the Monitoring API. Command names and options match the Official
/// CLI's XMCloud plugin.
/// </summary>
public static partial class CloudCommands
{
    public static Command Build()
    {
        var cloud = new Command("cloud", "Manage SitecoreAI projects, environments and deployments");
        cloud.Subcommands.Add(Login());
        cloud.Subcommands.Add(Logout());
        cloud.Subcommands.Add(Project());
        cloud.Subcommands.Add(Environment());
        cloud.Subcommands.Add(EditingHost());
        cloud.Subcommands.Add(Deployment());
        cloud.Subcommands.Add(Organization());
        return cloud;
    }

    // Options, spelled as the Official CLI spells them. Some carry a bare alias (ProjectName and the
    // like) because the Official CLI's parser accepts its options' internal names as aliases too.

    private static Option<string> ProjectId() => new("--project-id", "-id") { Description = "The project's ID", Required = true };
    private static Option<string> EnvironmentProjectId() => new("--project-id") { Description = "The project's ID", Required = true };
    private static Option<string> EnvironmentId() => new("--environment-id", "-id") { Description = "The environment's ID", Required = true };
    private static Option<string> DeploymentId() => new("--deployment-id", "-id") { Description = "The deployment's ID", Required = true };
    private static Option<string> ProjectName() => new("--name", "-n", "ProjectName") { Description = "The project's name, unique within the organization", Required = true };
    private static Option<string> EnvironmentName() => new("--name", "-n", "EnvironmentName") { Description = "The environment's name, unique within the project", Required = true };
    private static Option<bool> Force() => new("--force", "-f") { Description = "Do it without asking for confirmation" };
    private static Option<bool> NoWatch() => new("--no-watch") { Description = "Do not follow the deployment's progress" };
    private static Option<bool> NoStart() => new("--no-start") { Description = "Create the deployment but do not start it; start it later with `cloud deployment start`" };
    private static Option<bool> WaitForPostActions() => new("--waitForPostActions") { Description = "Also wait for the deployment's post actions to finish" };
    private static Option<int?> DnsTimeout() => new("--dns-timeout", "-dt") { Description = "Seconds to wait for the CM's host name to resolve after a deployment" };
    private static Option<string?> Timeout() => new("--timeout", "-to") { Description = "Seconds to wait for the transfer" };
    private static Option<bool> Upload() => new("--upload") { Description = "Upload the working folder as the deployment's source" };
    private static Option<string?> WorkingDir() => new("--working-dir", "-dir") { Description = "The folder to upload (default: the folder of sitecore.json); needs --upload" };
    private static Option<bool> AllowWrite() => new("--allow-write", "-aw") { Description = "Allow writing (pushing) to the environment" };
    private static Option<string?> Authority() => new("--authority", "--auth") { Description = "The identity authority (default: https://auth.sitecorecloud.io/)" };
    private static Option<string?> Audience() => new("--audience") { Description = "The audience of the access token (default: https://api.sitecorecloud.io)" };
    private static Option<bool> ClientCredentials() => new("--client-credentials", "--non-interactive") { Description = "Log in with a client ID and secret instead of in a browser" };
    private static Option<string?> ClientId() => new("--client-id") { Description = "The OAuth client ID" };
    private static Option<string?> ClientSecret() => new("--client-secret") { Description = "The OAuth client secret, for --client-credentials" };
    private static Option<string?> OrganizationId() => new("--organization-id", "-id") { Description = "The organization's ID (default: the logged-in organization)" };

    private static int? Seconds(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0 ? s
        : throw new CliException($"--timeout must be a number of seconds, not '{text}'.");

    private static Command Group(string name, string description, params Command[] commands)
    {
        var group = new Command(name, description);
        foreach (var c in commands)
        {
            group.Subcommands.Add(c);
        }
        return group;
    }

    /// <summary>Runs a cloud command with an authenticated SitecoreAI connection, checking the Deploy API's health alongside.</summary>
    private static void Action(Command command, Func<ParseResult, Session, CloudContext, CancellationToken, Task<int>> body) =>
        command.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var cloud = CloudContext.Open(session);
            var health = cloud.Deploy.ServiceHealthAsync(token);
            var exit = await body(parse, session, cloud, token);
            await ReportHealthAsync(session.Output, health);
            return exit;
        }, ct));

    private static async Task ReportHealthAsync(Output output, Task<JsonElement> health)
    {
        try
        {
            var report = await health;
            var status = CloudJson.String(report, "status");
            if (status is not null && status != "Healthy")
            {
                output.Warn($"SitecoreAI reports its services as {status}:");
                if (CloudJson.TryGet(report, "entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in entries.EnumerateObject())
                    {
                        output.Warn($"  {entry.Name}: {CloudJson.String(entry.Value, "status")}. {CloudJson.String(entry.Value, "description")}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is CloudApiException or HttpRequestException or TaskCanceledException)
        {
            output.Debug($"Could not read the health of the SitecoreAI services: {ex.Message}");
        }
    }

    private static void Confirm(bool force, string what)
    {
        if (force)
        {
            return;
        }
        if (Console.IsInputRedirected)
        {
            throw new CliException($"Refusing to {what} without confirmation. Pass --force to do it.");
        }
        while (true)
        {
            Console.Error.Write($"Are you sure you want to {what}? (y/n): ");
            switch (Console.ReadLine()?.Trim())
            {
                case "y" or "Y":
                    return;
                case "n" or "N" or null:
                    throw new CliException("Cancelled.");
            }
        }
    }

    // login / logout

    private static Command Login()
    {
        var allowWrite = AllowWrite();
        var authority = Authority();
        var audience = Audience();
        var deployHost = new Option<string?>("--xmcloudhost", "XmCloudDeployEndpoint") { Description = "The Deploy API host (default: https://xmclouddeploy-api.sitecorecloud.io/)" };
        var monitoringHost = new Option<string?>("--xmcloudmonitoringhost", "XmCloudMonitoringEndpoint") { Description = "The Monitoring API host (default: https://xmcloud-monitoring-api.sitecorecloud.io/)" };
        var clientId = ClientId();
        var clientSecret = ClientSecret();
        var clientCredentials = ClientCredentials();
        var cmd = new Command("login", "Log in to SitecoreAI") { allowWrite, authority, audience, deployHost, monitoringHost, clientId, clientSecret, clientCredentials };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var useClientCredentials = parse.GetValue(clientCredentials);
            var id = parse.GetValue(clientId);
            var secret = parse.GetValue(clientSecret);
            if (useClientCredentials && (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)))
            {
                throw new CliException("--client-credentials needs both --client-id and --client-secret.");
            }
            if (!useClientCredentials && !string.IsNullOrWhiteSpace(secret))
            {
                throw new CliException("--client-secret is only used with --client-credentials.");
            }
            var authorityUrl = Url(parse.GetValue(authority), Defaults.Authority, "--authority");
            var client = string.IsNullOrWhiteSpace(id) ? Defaults.SitecoreAiDeviceClientId : id;
            var audienceValue = string.IsNullOrWhiteSpace(parse.GetValue(audience)) ? Defaults.Audience : parse.GetValue(audience)!;
            var deploy = Url(parse.GetValue(deployHost), Defaults.DeployApi, "--xmcloudhost");
            var monitoring = Url(parse.GetValue(monitoringHost), Defaults.MonitoringApi, "--xmcloudmonitoringhost");
            var oidc = new OidcClient(Session.Http);
            TokenResponse result;
            if (useClientCredentials)
            {
                result = await oidc.ClientCredentialsAsync(authorityUrl, client, secret!, audienceValue, insecure: false, token);
            }
            else
            {
                if (Console.IsInputRedirected && System.Environment.GetEnvironmentVariable("CI") is not null)
                {
                    throw new CliException("A browser login is not possible here. Use --client-credentials, or set TERN_CLIENT_ID and TERN_CLIENT_SECRET.");
                }
                result = await oidc.DeviceCodeAsync(authorityUrl, client, audienceValue, insecure: false, Console.Error, openBrowser: true, token);
            }
            var login = new CloudLogin(authorityUrl, client, useClientCredentials ? secret : null, useClientCredentials, audienceValue, deploy, monitoring, parse.GetValue(allowWrite), result);
            var endpoint = CloudEndpoints.StoreLogin(session.Store, login, DateTimeOffset.UtcNow);
            session.Store.Save();
            var organization = Jwt.Claim(result.AccessToken, CloudEndpoints.OrganizationClaim);
            session.Output.Info($"Logged in to SitecoreAI{(organization is null ? string.Empty : $" (organization {organization})")}; the login is stored as endpoint '{endpoint.Name}'.");
            session.Output.RawJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("endpoint", endpoint.Name);
                w.WriteString("host", endpoint.Host);
                w.WriteString("organizationId", organization);
                w.WriteBoolean("allowWrite", endpoint.AllowWrite);
                w.WriteEndObject();
            });
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static string Url(string? value, string fallback, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new CliException($"{option} must be an absolute URL, not '{value}'.");
        }
        return value;
    }

    private static Command Logout()
    {
        var cmd = new Command("logout", "Log out of SitecoreAI and forget the environments connected through it");
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var cloud = CloudEndpoints.Find(session.Store);
            if (cloud is null)
            {
                session.Output.Line($"Endpoint {CloudEndpoints.Name} is not present.");
                session.Output.RawJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("removed");
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
                return Task.FromResult(ExitCodes.Success);
            }
            var authority = cloud.Authority ?? Defaults.Authority;
            var removed = CloudEndpoints.Logout(session.Store);
            session.Store.Save();
            foreach (var name in removed)
            {
                session.Output.Debug($"Removed endpoint '{name}'.");
            }
            session.Output.Line($"Successfully logged out from {CloudEndpoints.Name}");
            session.Output.Info($"To end the browser session as well, open {authority.TrimEnd('/')}/v2/logout");
            session.Output.RawJson(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("removed");
                removed.ToList().ForEach(w.WriteStringValue);
                w.WriteEndArray();
                w.WriteString("logoutUrl", $"{authority.TrimEnd('/')}/v2/logout");
                w.WriteEndObject();
            });
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    // project

    private static Command Project()
    {
        var list = new Command("list", "List the organization's projects");
        Action(list, async (parse, session, cloud, ct) =>
        {
            var projects = CloudJson.Items(await cloud.Deploy.ProjectsAsync(ct)).ToList();
            session.Output.RawJson(w => CloudOutput.WriteArray(w, projects, CloudOutput.Project));
            session.Output.Line($"Found {projects.Count} project(s)");
            foreach (var p in projects)
            {
                PrintObject(session.Output, $"{CloudJson.String(p, "name")} Project detailed information:", p, CloudOutput.Project, CloudOutput.RegionOverride(p));
                session.Output.Line();
            }
            return ExitCodes.Success;
        });

        var createName = ProjectName();
        var create = new Command("create", "Create a project in the organization") { createName };
        Action(create, async (parse, session, cloud, ct) =>
        {
            var project = await cloud.Deploy.CreateProjectAsync(parse.GetValue(createName)!, ct);
            var region = CloudOutput.RegionOverride(project);
            session.Output.RawJson(w => CloudOutput.WriteObject(w, project, CloudOutput.CreatedProject, region));
            var id = CloudJson.String(project, "id");
            session.Output.Line("Project created.");
            session.Output.Line($"'{CloudJson.String(project, "name")}' Project was created within '{region["region"]}' Region for the '{CloudJson.String(project, "organizationName")}' Organization.");
            session.Output.Line($"Its Project Id is '{id}'.");
            if (session.Output.Verbose)
            {
                session.Output.Line();
                CloudOutput.Table(project, CloudOutput.CreatedProject, region).ToList().ForEach(session.Output.Line);
            }
            session.Output.Line("Add an environment to it with:");
            session.Output.Line($"tern cloud environment create --project-id {id} --name <environment-name>");
            return ExitCodes.Success;
        });

        var updateId = ProjectId();
        var updateName = ProjectName();
        var update = new Command("update", "Rename a project") { updateId, updateName };
        Action(update, async (parse, session, cloud, ct) =>
        {
            var id = parse.GetValue(updateId)!;
            var previous = CloudJson.String(await cloud.Deploy.ProjectAsync(id, ct), "name");
            await cloud.Deploy.UpdateProjectAsync(id, parse.GetValue(updateName)!, ct);
            // The Official CLI's --json prints null for a command that returns nothing.
            session.Output.RawJson(w => w.WriteNullValue());
            session.Output.Line("Project updated.");
            session.Output.Line($"Project '{id}' was renamed from '{previous}' to '{parse.GetValue(updateName)}'.");
            return ExitCodes.Success;
        });

        var deleteId = ProjectId();
        var deleteForce = Force();
        var delete = new Command("delete", "Delete a project") { deleteId, deleteForce };
        Action(delete, async (parse, session, cloud, ct) =>
        {
            var id = parse.GetValue(deleteId)!;
            var project = await cloud.Deploy.ProjectAsync(id, ct);
            var name = CloudJson.String(project, "name");
            Confirm(parse.GetValue(deleteForce), $"delete project '{name}' ({id})");
            await cloud.Deploy.DeleteProjectAsync(id, ct);
            session.Output.RawJson(w => w.WriteNullValue());
            session.Output.Line($"Project '{name}' ({id}) was deleted from the '{CloudJson.String(project, "organizationName")}' Organization.");
            return ExitCodes.Success;
        });

        var infoId = ProjectId();
        var info = new Command("info", "Show a project") { infoId };
        Action(info, async (parse, session, cloud, ct) =>
        {
            var project = await cloud.Deploy.ProjectAsync(parse.GetValue(infoId)!, ct);
            var region = CloudOutput.RegionOverride(project);
            session.Output.RawJson(w => CloudOutput.WriteObject(w, project, CloudOutput.Project, region));
            PrintObject(session.Output, $"{CloudJson.String(project, "name")} Project detailed information:", project, CloudOutput.Project, region);
            return ExitCodes.Success;
        });

        return Group("project", "Manage the organization's SitecoreAI projects", list, create, update, delete, info);
    }

    private static void PrintObject(Output output, string heading, JsonElement item, IReadOnlyList<CloudField> shape, IReadOnlyDictionary<string, string>? overrides = null)
    {
        output.Line(heading);
        foreach (var row in CloudOutput.Table(item, shape, overrides))
        {
            output.Line(row);
        }
    }

    // organization

    private static Command Organization()
    {
        var infoId = OrganizationId();
        var info = new Command("info", "Show the organization") { infoId };
        Action(info, async (parse, session, cloud, ct) =>
        {
            var organization = await cloud.Deploy.OrganizationAsync(await cloud.OrganizationIdAsync(parse.GetValue(infoId), ct), ct);
            session.Output.RawJson(w => CloudOutput.WriteObject(w, organization, CloudOutput.Organization));
            CloudOutput.Table(organization, CloudOutput.Organization).ToList().ForEach(session.Output.Line);
            return ExitCodes.Success;
        });

        var healthId = OrganizationId();
        var health = new Command("health", "Show the health of the organization's resources") { healthId };
        Action(health, async (parse, session, cloud, ct) =>
        {
            var report = await cloud.Deploy.OrganizationHealthAsync(await cloud.OrganizationIdAsync(parse.GetValue(healthId), ct), ct);
            session.Output.RawJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("Status", CloudJson.String(report, "status"));
                w.WriteStartArray("Resources");
                foreach (var r in CloudJson.TryGet(report, "resources", out var resources) ? CloudJson.Items(resources) : [])
                {
                    w.WriteStartObject();
                    w.WriteString("Status", CloudJson.String(r, "status"));
                    w.WriteString("Name", CloudJson.String(r, "name"));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            session.Output.Line($"Organization resources health status: {CloudJson.String(report, "status")}");
            return ExitCodes.Success;
        });

        var licenseId = OrganizationId();
        var license = new Command("license", "Download the organization's license.xml into the folder of sitecore.json") { licenseId };
        Action(license, async (parse, session, cloud, ct) =>
        {
            var bytes = await cloud.Deploy.LicenseAsync(await cloud.OrganizationIdAsync(parse.GetValue(licenseId), ct), ct);
            var folder = session.ProjectDirectory;
            var file = Path.Combine(folder, "license.xml");
            await File.WriteAllBytesAsync(file, bytes, ct);
            session.Output.RawJson(w => w.WriteNullValue());
            session.Output.Line($"Organization license has been saved to {folder}.");
            return ExitCodes.Success;
        });

        return Group("organization", "Show the SitecoreAI organization", info, health, license);
    }
}
