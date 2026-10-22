using System.CommandLine;

using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Planning;
using Tern.Remote.Auth;
using Tern.Remote.Management;

namespace Tern.Cli.Common;

/// <summary>The options most commands share, named exactly as the Official CLI names them.</summary>
public static class CommonOptions
{
    public static readonly Option<string?> Config = new("--config", "-c") { Description = "Path to root sitecore.json directory", Recursive = true };
    public static readonly Option<bool> Verbose = new("--verbose", "-v") { Description = "Write some additional diagnostic and performance data", Recursive = true };
    public static readonly Option<bool> Trace = new("--trace", "-t") { Description = "Write more additional diagnostic and performance data", Recursive = true };
    public static readonly Option<bool> Json = new("--json") { Description = "Write one JSON document to stdout instead of human-readable output", Recursive = true };
    public static readonly Option<string?> Record = new("--record") { Description = "Write every request to the CM, with secrets removed, to this folder", Recursive = true };

    public static Option<string?> EnvironmentName() =>
        new("--environment-name", "-n") { Description = "Named Sitecore environment to use. If not specified, the environment that was set as default or 'default' will be resolved." };

    public static Option<string[]> Include() =>
        new("--include", "-i") { Description = "Module configurations to include. Wildcards and multiple values are allowed", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };

    public static Option<string[]> Exclude() =>
        new("--exclude", "-e") { Description = "Module configurations to explicitly exclude. Wildcards and multiple values are allowed", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };

    /// <summary>Runs a command body with a Session, turning known failures into clean messages and exit codes.</summary>
    public static async Task<int> RunAsync(ParseResult parse, Func<Session, CancellationToken, Task<int>> body, CancellationToken ct)
    {
        var output = new Output(parse.GetValue(Json), parse.GetValue(Verbose), parse.GetValue(Trace));
        using var session = new Session(parse.GetValue(Config), output) { RecordDirectory = parse.GetValue(Record) };
        try
        {
            return await body(session, ct);
        }
        catch (CliException ex)
        {
            output.Error(ex.Message);
            return ex.ExitCode;
        }
        catch (PlanOutOfDateException ex)
        {
            output.Error(ex.Message);
            return ExitCodes.PlanOutOfDate;
        }
        catch (Exception ex) when (ex is ConfigurationException or ItemFileFormatException or ManagementException or AuthenticationException or SourceMissingException or NonUniquePathException or InvalidDataException or Tern.Remote.Cloud.CloudApiException or DirectoryNotFoundException)
        {
            output.Error(ex.Message);
            if (output.Trace)
            {
                output.Error(ex.ToString());
            }
            return ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            output.Error("Cancelled.");
            return ExitCodes.Failure;
        }
        catch (HttpRequestException ex)
        {
            output.Error($"Could not reach the server: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
