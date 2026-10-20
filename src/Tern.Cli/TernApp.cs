using System.CommandLine;
using System.Reflection;

namespace Tern.Cli;

public static class TernApp
{
    public static Task<int> RunAsync(string[] args)
    {
        var root = new RootCommand("Tern: a drop-in, Native AOT replacement for the Sitecore CLI.");
        foreach (var option in root.Options.OfType<VersionOption>().ToList())
        {
            root.Options.Remove(option);
        }
        var version = new Option<bool>("--version") { Description = "Show Tern's version and the Sitecore CLI line it is a drop-in for." };
        root.Options.Add(version);
        root.SetAction(parse =>
        {
            if (parse.GetValue(version))
            {
                Console.Out.WriteLine(VersionText);
                return 0;
            }
            return 0;
        });
        return root.Parse(args).InvokeAsync();
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
