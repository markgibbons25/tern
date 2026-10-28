using System.CommandLine;

using Tern.Cli.Common;
using Tern.Remote.Auth;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary><c>login</c>, <c>connect</c>, <c>env</c>, <c>init</c> and <c>plugin</c>.</summary>
public static class LoginCommands
{
    public static Command Login()
    {
        var environment = CommonOptions.EnvironmentName();
        var authority = new Option<string?>("--authority", "--auth") { Description = "Identity authority for the environment, i.e. identity server or AAD tenant URL" };
        var cm = new Option<string?>("--cm") { Description = "Sitecore content management hostname to connect to" };
        var allowWrite = new Option<bool?>("--allow-write") { Description = "Enables writing data to the environment" };
        var clientCredentials = new Option<bool?>("--client-credentials") { Description = "Use client credentials (for CI/CD, shared secret)" };
        var clientId = new Option<string?>("--client-id") { Description = "The OAuth ClientID to send. Defaults to 'Device' for device auth" };
        var clientSecret = new Option<string?>("--client-secret") { Description = "The OAuth client secret to send. Only used for client credentials authentication" };
        var insecure = new Option<bool>("--insecure", "-k") { Description = "Allows to use insecure authority requests during login" };
        var reference = new Option<string?>("--ref", "-r") { Description = "Reference to base environment" };
        var audience = new Option<string?>("--audience") { Description = "The unique identifier of the audience for an issued token" };
        var asDefault = new Option<bool>("--as-default") { Description = "Sets environment as default in user configuration" };
        var cmd = new Command("login", "Authenticate the CLI with a Sitecore instance") { environment, authority, cm, allowWrite, clientCredentials, clientId, clientSecret, insecure, reference, audience, asDefault };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var store = session.Store;
            var name = parse.GetValue(environment) ?? store.DefaultEndpointName;
            var endpoint = store.GetOrAdd(name);
            if (parse.GetValue(reference) is { } r)
            {
                endpoint.Ref = r;
            }
            if (parse.GetValue(cm) is { } host)
            {
                endpoint.Host = host;
            }
            if (parse.GetValue(allowWrite) is { } write)
            {
                endpoint.AllowWrite = write;
            }
            if (parse.GetValue(authority) is { } a)
            {
                endpoint.Authority = a;
            }
            if (parse.GetValue(audience) is { } aud)
            {
                endpoint.Audience = aud;
            }
            if (parse.GetValue(insecure))
            {
                endpoint.Insecure = true;
            }
            var useClientCredentials = parse.GetValue(clientCredentials) ?? endpoint.UseClientCredentials;
            endpoint.UseClientCredentials = useClientCredentials;
            endpoint.ClientId = parse.GetValue(clientId) ?? endpoint.ClientId ?? (useClientCredentials ? null : "Device");
            if (parse.GetValue(clientSecret) is { } secret)
            {
                endpoint.ClientSecret = secret;
            }
            if (endpoint.Authority is null)
            {
                throw new CliException("--authority is required the first time you log in to an endpoint.");
            }
            if (parse.GetValue(asDefault))
            {
                store.DefaultEndpointName = endpoint.Name;
            }

            if (session.Root.CacheAuthenticationToken && endpoint.AccessToken is not null && endpoint.AccessTokenExpires is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(5) && parse.GetValue(clientSecret) is null)
            {
                store.Save();
                session.Output.Info($"Already logged in to '{endpoint.Name}'; the token is valid until {exp:u}.");
                return ExitCodes.Success;
            }

            var oidc = new OidcClient(Session.Http);
            TokenResponse result;
            if (useClientCredentials)
            {
                // Without --client-id/--client-secret, take them from TERN_* variables (or the project's .env); they are not stored.
                var fromEnvironment = endpoint.ClientId is null || endpoint.ClientSecret is null ? EnvironmentCredentials.For(endpoint.Name, session.EnvironmentVariables) : null;
                var id = endpoint.ClientId ?? fromEnvironment?.ClientId ?? throw new CliException("--client-id is required for client credentials, or set TERN_CLIENT_ID and TERN_CLIENT_SECRET.");
                var secretValue = endpoint.ClientSecret ?? fromEnvironment?.ClientSecret ?? throw new CliException("--client-secret is required for client credentials, or set TERN_CLIENT_ID and TERN_CLIENT_SECRET.");
                result = await oidc.ClientCredentialsAsync(endpoint.Authority, id, secretValue, endpoint.Audience, endpoint.Insecure, token);
            }
            else
            {
                if (Console.IsInputRedirected && Environment.GetEnvironmentVariable("CI") is not null)
                {
                    throw new CliException("Interactive login is not possible here. Use --client-credentials, or set TERN_CLIENT_ID and TERN_CLIENT_SECRET.");
                }
                result = await oidc.DeviceCodeAsync(endpoint.Authority, endpoint.ClientId!, endpoint.Audience, endpoint.Insecure, Console.Error, openBrowser: true, token);
            }
            Store(endpoint, result);
            store.Save();
            session.Output.Info($"Logged in to '{endpoint.Name}'.");
            session.Output.Document("login", w =>
            {
                w.WriteString("endpoint", endpoint.Name);
                w.WriteString("host", endpoint.Host);
                w.WriteBoolean("allowWrite", endpoint.AllowWrite);
            });
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    internal static void Store(Endpoint endpoint, TokenResponse result)
    {
        endpoint.AccessToken = result.AccessToken;
        if (result.RefreshToken is not null)
        {
            endpoint.RefreshToken = result.RefreshToken;
        }
        endpoint.ExpiresIn = result.ExpiresIn;
        endpoint.LastUpdated = DateTimeOffset.UtcNow;
    }

    public static Command Connect()
    {
        var environment = new Option<string?>("--environment-name", "-n") { Description = "Named Sitecore environment to connect", Required = true };
        var cm = new Option<string?>("--cm") { Description = "Sitecore content management hostname to connect to", Required = true };
        var reference = new Option<string?>("--ref", "-r") { Description = "Reference to base environment", Required = true };
        var allowWrite = new Option<bool?>("--allow-write") { Description = "Enables writing data to the environment" };
        var cmd = new Command("connect", "Connect a named environment to a CM using the login of another environment") { environment, cm, reference, allowWrite };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var endpoint = session.Store.GetOrAdd(parse.GetValue(environment)!);
            endpoint.Ref = parse.GetValue(reference);
            endpoint.Host = parse.GetValue(cm);
            endpoint.AllowWrite = parse.GetValue(allowWrite) ?? false;
            if (session.Store.Find(endpoint.Ref) is null)
            {
                throw new CliException($"There is no endpoint named '{endpoint.Ref}' to refer to.");
            }
            session.Store.Save();
            session.Output.Info($"Connected '{endpoint.Name}' to {endpoint.Host} using the login of '{endpoint.Ref}'.");
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    public static Command Env()
    {
        var env = new Command("env", "Manage environments");
        env.Aliases.Add("environment");

        var current = new Command("current", "Show the default environment");
        current.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var name = session.Store.DefaultEndpointName;
            session.Output.Document("env current", w => w.WriteString("endpoint", name));
            session.Output.Line(name);
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        env.Subcommands.Add(current);

        var setName = CommonOptions.EnvironmentName();
        var setDefault = new Command("set-default", "Set the default environment") { setName };
        setDefault.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var endpoint = session.Endpoint(parse.GetValue(setName), announce: false);
            session.Store.DefaultEndpointName = endpoint.Name;
            session.Store.Save();
            session.Output.Info($"'{endpoint.Name}' is now the default environment.");
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        env.Subcommands.Add(setDefault);

        var list = new Command("list", "List environments");
        list.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var all = session.Store.All.ToList();
            session.Output.Document("env list", w =>
            {
                w.WriteString("default", session.Store.DefaultEndpointName);
                w.WriteStartArray("endpoints");
                foreach (var e in all)
                {
                    w.WriteStartObject();
                    w.WriteString("name", e.Name);
                    w.WriteString("host", e.Host);
                    w.WriteString("ref", e.Ref);
                    w.WriteBoolean("allowWrite", e.AllowWrite);
                    w.WriteString("authority", e.Authority);
                    w.WriteBoolean("shared", e.Shared is not null);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            foreach (var e in all)
            {
                var marker = e.Name.Equals(session.Store.DefaultEndpointName, StringComparison.OrdinalIgnoreCase) ? "* " : "  ";
                session.Output.Line($"{marker}{e.Name}: {e.Host}{(e.Ref is { } r ? $" (login from {r})" : string.Empty)}{(e.AllowWrite ? string.Empty : " [read-only]")}{(e.Shared is null ? string.Empty : " (tern.json)")}");
            }
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        env.Subcommands.Add(list);

        var infoName = CommonOptions.EnvironmentName();
        var info = new Command("info", "Show information about the environment's CM") { infoName };
        info.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var endpoint = session.Endpoint(parse.GetValue(infoName), announce: false);
            var system = new SystemApi(session.Management(endpoint));
            var details = await system.InfoAsync(token);
            var (queries, mutations) = await system.RootFieldsAsync(token);
            session.Output.Document("env info", w =>
            {
                w.WriteString("endpoint", endpoint.Name);
                w.WriteString("host", endpoint.Host);
                w.WriteString("sitecore", details.SitecoreInfo);
                w.WriteString("managementServices", details.ManagementServicesVersion);
                w.WriteStartArray("queries");
                queries.Order().ToList().ForEach(w.WriteStringValue);
                w.WriteEndArray();
                w.WriteStartArray("mutations");
                mutations.Order().ToList().ForEach(w.WriteStringValue);
                w.WriteEndArray();
            });
            session.Output.Line($"Endpoint:            {endpoint.Name} ({endpoint.Host})");
            session.Output.Line($"Sitecore:            {details.SitecoreInfo}");
            session.Output.Line($"Management Services: {details.ManagementServicesName} {details.ManagementServicesVersion}");
            session.Output.Line($"Features:            {string.Join(", ", queries.Concat(mutations).Order())}");
            return ExitCodes.Success;
        }, ct));
        env.Subcommands.Add(info);
        return env;
    }

    public static Command Init()
    {
        var cmd = new Command("init", "Create a new sitecore.json in the current folder");
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var dir = session.ConfigDirectory;
            var file = Path.Join(dir, "sitecore.json");
            if (File.Exists(file))
            {
                throw new CliException($"{file} already exists.");
            }
            File.WriteAllText(file, """
                {
                  "$schema": "./.sitecore/schemas/RootConfigurationFile.schema.json",
                  "modules": [
                    "src/*/*.module.json"
                  ],
                  "plugins": [],
                  "serialization": {
                    "defaultMaxRelativeItemPathLength": 100,
                    "defaultModuleRelativeSerializationPath": "serialization",
                    "removeOrphansForRoles": true,
                    "removeOrphansForUsers": true,
                    "continueOnItemFailure": false,
                    "excludedFields": []
                  },
                  "settings": {
                    "telemetryEnabled": false,
                    "cacheAuthenticationToken": true,
                    "versionComparisonEnabled": true,
                    "apiClientTimeoutInMinutes": 5
                  }
                }
                """.ReplaceLineEndings());
            session.Output.Info($"Created {file}.");
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    public static Command Plugin()
    {
        var cmd = new Command("plugin", "The Official CLI's plugin commands. Tern has the plugins' commands built in.");
        foreach (var name in new[] { "add", "remove", "list", "init" })
        {
            var sub = new Command(name, $"Not needed with Tern: the plugin commands are built in");
            // The Official CLI's options are accepted and ignored, so scripts that pass them still run.
            if (name is "add" or "remove")
            {
                sub.Options.Add(new Option<string?>("--name", "-n") { Description = name == "add" ? "Name of the plugin to install into the current project" : "Name of the plugin to remove from the current project" });
            }
            if (name is "add" or "init")
            {
                sub.Options.Add(new Option<string?>("--version") { Description = name == "add" ? "Version of the plugin to install" : "Version of the plugins to init" });
            }
            if (name == "init")
            {
                sub.Options.Add(new Option<bool>("--overwrite") { Description = "Allow overwriting existing plugin" });
                sub.Options.Add(new Option<bool>("--prerelease") { Description = "Allow pre-release versions for plugins" });
            }
            sub.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
            {
                session.Output.Info("Tern does not load plugins; the serialization, publishing, indexing, resource package, database and cloud commands are built in (ADR 0002).");
                if (name == "list")
                {
                    foreach (var p in session.Root.Plugins)
                    {
                        session.Output.Line($"{p} (provided by Tern)");
                    }
                }
                return Task.FromResult(ExitCodes.Success);
            }, ct));
            cmd.Subcommands.Add(sub);
        }
        return cmd;
    }
}
