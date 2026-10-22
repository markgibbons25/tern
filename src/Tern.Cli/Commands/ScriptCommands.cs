using System.CommandLine;
using System.Text.Json;

using Tern.Cli.Common;
using Tern.Remote.Authoring;
using Tern.Remote.Spe;

namespace Tern.Cli.Commands;

/// <summary>
/// M5 commands that reach past Management Services: <c>script run</c> (SPE remoting),
/// <c>authoring query</c> and <c>item set</c> (the Authoring GraphQL API).
/// </summary>
public static class ScriptCommands
{
    public static Command Script()
    {
        var script = new Command("script", "Run Sitecore PowerShell Extensions scripts on a CM");
        var environment = CommonOptions.EnvironmentName();
        var file = new Option<string?>("--file", "-f") { Description = "A .ps1 file to run" };
        var inline = new Option<string?>("--script", "-s") { Description = "Script text to run" };
        var user = new Option<string?>("--spe-user") { Description = "Sitecore user to run as (default: TERN_SPE_USER, then admin)" };
        var password = new Option<string?>("--spe-password") { Description = "That user's password, for Basic authentication (default: TERN_SPE_PASSWORD)" };
        var secret = new Option<string?>("--spe-secret") { Description = "SPE's shared secret, for token authentication (default: TERN_SPE_SECRET)" };
        var run = new Command("run", "Run a script and print what it writes") { environment, file, inline, user, password, secret };
        run.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var text = parse.GetValue(file) is { } path ? await File.ReadAllTextAsync(path, token)
                : parse.GetValue(inline) ?? throw new CliException("Pass --file or --script.");
            var endpoint = session.Endpoint(parse.GetValue(environment));
            // A script can do anything, so it runs only where writes are allowed.
            if (!endpoint.AllowWrite)
            {
                throw new CliException(ReadOnlyEndpoint.Message(endpoint, "run scripts on it"));
            }
            var userName = parse.GetValue(user) ?? Environment.GetEnvironmentVariable("TERN_SPE_USER") ?? "admin";
            SpeCredentials credentials =
                (parse.GetValue(secret) ?? Environment.GetEnvironmentVariable("TERN_SPE_SECRET")) is { } s ? new SpeCredentials.SharedSecret(userName, s)
                : (parse.GetValue(password) ?? Environment.GetEnvironmentVariable("TERN_SPE_PASSWORD")) is { } p ? new SpeCredentials.Basic(userName, p)
                : throw new CliException("SPE needs credentials: pass --spe-password or --spe-secret, or set TERN_SPE_PASSWORD or TERN_SPE_SECRET.");
            var output = await new SpeClient(Session.Http, endpoint.Host!, credentials).RunAsync(text, token);
            if (session.Output.Json)
            {
                session.Output.Document("script run", w => w.WriteString("output", output));
            }
            else
            {
                Console.Out.Write(output);
                if (!output.EndsWith('\n'))
                {
                    Console.Out.WriteLine();
                }
            }
            return ExitCodes.Success;
        }, ct));
        script.Subcommands.Add(run);
        return script;
    }

    public static Command Authoring()
    {
        var authoring = new Command("authoring", "Use the Authoring GraphQL API, which does not need an Administrator");
        var environment = CommonOptions.EnvironmentName();
        var query = new Option<string?>("--query", "-q") { Description = "A GraphQL document" };
        var file = new Option<string?>("--file", "-f") { Description = "A file holding the GraphQL document" };
        var variables = new Option<string?>("--variables") { Description = "Variables as a JSON object" };
        var run = new Command("query", "Send a query or mutation and print the JSON response") { environment, query, file, variables };
        run.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var document = parse.GetValue(file) is { } path ? await File.ReadAllTextAsync(path, token)
                : parse.GetValue(query) ?? throw new CliException("Pass --query or --file.");
            var endpoint = session.Endpoint(parse.GetValue(environment));
            if (!endpoint.AllowWrite && GraphQlDocument.HasMutation(document))
            {
                throw new CliException(ReadOnlyEndpoint.Message(endpoint, "send mutations to it"));
            }
            using var response = await new AuthoringClient(Session.Http, endpoint.Host!, session.Tokens(endpoint)).QueryAsync(document, parse.GetValue(variables), token);
            using var stdout = Console.OpenStandardOutput();
            using (var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                response.RootElement.WriteTo(w);
            }
            stdout.Write("\n"u8);
            var failed = response.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;
            return failed ? ExitCodes.Failure : ExitCodes.Success;
        }, ct));
        authoring.Subcommands.Add(run);
        return authoring;
    }

    public static Command ItemSet()
    {
        var target = new Argument<string>("path-or-id") { Description = "The item to change" };
        var environment = CommonOptions.EnvironmentName();
        var database = new Option<string>("--database", "-d") { Description = "Database", DefaultValueFactory = _ => "master" };
        var language = new Option<string?>("--language", "-l") { Description = "Language of the version to change (default: the CM's default)" };
        var version = new Option<int?>("--version") { Description = "Version number to change (default: the latest)" };
        var fields = new Option<string[]>("--field") { Description = "<field name>=<value>; repeat for more fields", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var reset = new Option<string[]>("--reset") { Description = "Field names to reset to their standard value", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var cmd = new Command("set", "Change field values through the Authoring API") { target, environment, database, language, version, fields, reset };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var changes = parse.GetValue(fields)!.Select(f =>
            {
                var eq = f.IndexOf('=');
                return eq <= 0 ? throw new CliException($"'{f}' is not <field name>=<value>.") : (Name: f[..eq], Value: (string?)f[(eq + 1)..]);
            }).Concat(parse.GetValue(reset)!.Select(r => (Name: r, Value: (string?)null))).ToList();
            if (changes.Count == 0)
            {
                throw new CliException("Pass at least one --field or --reset.");
            }
            var endpoint = session.Endpoint(parse.GetValue(environment));
            if (!endpoint.AllowWrite)
            {
                throw new CliException(ReadOnlyEndpoint.Message(endpoint, null));
            }
            using var result = await new AuthoringClient(Session.Http, endpoint.Host!, session.Tokens(endpoint))
                .UpdateItemAsync(parse.GetValue(target)!, parse.GetValue(database)!, parse.GetValue(language), parse.GetValue(version), changes, token);
            var item = result.RootElement.GetProperty("data").GetProperty("updateItem").GetProperty("item");
            session.Output.Document("item set", w =>
            {
                w.WritePropertyName("item");
                item.WriteTo(w);
            });
            session.Output.Line($"Updated {item.GetProperty("path").GetString()} ({item.GetProperty("itemId").GetString()}): {string.Join(", ", changes.Select(c => c.Name))}");
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }
}
