using Tern.Core.Configuration;
using Tern.Core.ItemFiles;

namespace Tern.Core.Tests.ItemFiles;

/// <summary>
/// For each project in TERN_PROJECTS (folders holding a sitecore.json, separated by ';'), checks
/// that every Item file sits exactly where Tern maps its item path. Skipped when the variable is unset.
/// </summary>
public class CorpusPathMappingTests
{
    [Fact]
    public void Every_item_file_is_where_Tern_maps_it()
    {
        var projects = Environment.GetEnvironmentVariable("TERN_PROJECTS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(projects), "TERN_PROJECTS is not set.");

        var failures = new List<string>();
        var count = 0;
        var notIncluded = 0;
        foreach (var project in projects!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var root = ConfigurationLoader.LoadRoot(Path.Combine(project, "sitecore.json"));
            var modules = ConfigurationLoader.Validate(ConfigurationLoader.LoadModules(root));
            foreach (var include in modules.SelectMany(m => m.Includes))
            {
                if (!Directory.Exists(include.PhysicalRoot))
                {
                    continue;
                }
                var paths = new ItemFilePaths(include);
                foreach (var file in Directory.EnumerateFiles(include.PhysicalRoot, "*.yml", SearchOption.AllDirectories))
                {
                    count++;
                    var item = ItemFileReader.Read(File.ReadAllBytes(file), file);
                    var mapped = paths.FileFor(ItemPath.Parse(item.Path));
                    if (mapped is null)
                    {
                        // A file the Include no longer covers, e.g. after a rule was added. `ser validate`
                        // reports these; they are not a mapping fault.
                        notIncluded++;
                    }
                    else if (!string.Equals(Path.GetFullPath(mapped), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{file}: mapped to {mapped}");
                    }
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{count} files mapped, {notIncluded} no longer included, {failures.Count} failed.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} files failed:\n" + string.Join('\n', failures.Take(25)));
    }
}
