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
        var version = new Option<bool>("--version") { Description = "Show Tern's version." };
        root.Options.Add(version);
        root.Options.Add(CommonOptions.Config);
        root.Options.Add(CommonOptions.Verbose);
        root.Options.Add(CommonOptions.Trace);
        root.Options.Add(CommonOptions.Json);
        root.Options.Add(CommonOptions.Record);
        root.Options.Add(CommonOptions.Pager);
        root.Options.Add(CommonOptions.Quiet);

        root.Subcommands.Add(LoginCommands.Login());
        root.Subcommands.Add(LoginCommands.Connect());
        root.Subcommands.Add(LoginCommands.Env());
        root.Subcommands.Add(LoginCommands.Init());
        root.Subcommands.Add(LoginCommands.Plugin());
        root.Subcommands.Add(SerializationCommands.Build());
        root.Subcommands.Add(ApplyCommand.Build());
        root.Subcommands.Add(ContentCommands.Build());
        root.Subcommands.Add(ItemCommands.Build());
        root.Subcommands.Add(PublishCommands.Build());
        root.Subcommands.Add(PublishCommands.BuildIndex());
        root.Subcommands.Add(PublishCommands.BuildDbCleanup());
        root.Subcommands.Add(ItemResCommands.Build());
        root.Subcommands.Add(ScriptCommands.Script());
        root.Subcommands.Add(ScriptCommands.Authoring());
        root.Subcommands.Add(CloudCommands.Build());

        root.SetAction(async (parse, ct) =>
        {
            if (parse.GetValue(version))
            {
                Console.Out.WriteLine(VersionText);
                return ExitCodes.Success;
            }
            Console.Out.WriteLine(VersionText);
            Console.Out.WriteLine("Run `tern --help` to see the commands.");
            // --version stays instant for scripts; plain `tern` is run by a person, so it checks for updates.
            var output = new Output(parse.GetValue(CommonOptions.Json), verbose: false, trace: false);
            if (UpdateCheck.Start(output) is { } updateCheck)
            {
                await updateCheck.ReportAsync(output);
            }
            return ExitCodes.Success;
        });
        return root;
    }

    /// <summary>Tern's own version, without the commit hash.</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(TernApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
    }

    public static string VersionText => $"tern {Version}";
}
