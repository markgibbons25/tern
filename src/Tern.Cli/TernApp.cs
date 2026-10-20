using System.CommandLine;
using System.Reflection;
using Tern.Cli.Commands;
using Tern.Cli.Common;

namespace Tern.Cli;

public static class TernApp
{
    public static Task<int> RunAsync(string[] args) => Build().Parse(args).InvokeAsync();

    public static RootCommand Build()
    {
        var root = new RootCommand("Tern: a drop-in, Native AOT replacement for the Sitecore CLI.");
        foreach (var option in root.Options.OfType<VersionOption>().ToList())
        {
            root.Options.Remove(option);
        }
        var version = new Option<bool>("--version") { Description = "Show Tern's version and the Sitecore CLI line it is a drop-in for." };
        root.Options.Add(version);
        root.Options.Add(CommonOptions.Config);
        root.Options.Add(CommonOptions.Verbose);
        root.Options.Add(CommonOptions.Trace);
        root.Options.Add(CommonOptions.Json);
        root.Options.Add(CommonOptions.Record);

        root.Subcommands.Add(LoginCommands.Login());
        root.Subcommands.Add(LoginCommands.Connect());
        root.Subcommands.Add(LoginCommands.Env());
        root.Subcommands.Add(LoginCommands.Init());
        root.Subcommands.Add(LoginCommands.Plugin());
        root.Subcommands.Add(SerializationCommands.Build());
        root.Subcommands.Add(ApplyCommand.Build());
        root.Subcommands.Add(ItemCommands.Build());
        root.Subcommands.Add(PublishCommands.Build());
        root.Subcommands.Add(PublishCommands.BuildIndex());
        root.Subcommands.Add(PublishCommands.BuildDbCleanup());

        root.SetAction(parse =>
        {
            if (parse.GetValue(version))
            {
                Console.Out.WriteLine(VersionText);
                return ExitCodes.Success;
            }
            Console.Out.WriteLine(VersionText);
            Console.Out.WriteLine("Run `tern --help` to see the commands.");
            return ExitCodes.Success;
        });
        return root;
    }

    public static string VersionText
    {
        get
        {
            var assembly = typeof(TernApp).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+');
            var ternVersion = plus < 0 ? informational : informational[..plus];
            var line = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "OfficialCliLine")?.Value ?? "?";
            return $"tern {ternVersion} (drop-in for Sitecore CLI {line})";
        }
    }
}
