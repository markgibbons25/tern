using System.Net;
using System.Text.Json;
using Tern.Cli.Commands;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Remote.Management;

namespace Tern.Core.Tests.Configuration;

public class ContentScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tern-content-{Guid.NewGuid():N}");

    private static Module M(string file, params string[] paths)
    {
        var module = new Module
        {
            Namespace = "Project." + file,
            SourcePath = Path.Combine("/project", file + ".module.json"),
            Includes = paths.Select((p, i) => new Include { Name = $"{file}-{i}", Path = ItemPath.Parse(p) }).ToList(),
        };
        foreach (var include in module.Includes)
        {
            include.Module = module;
        }
        return module;
    }

    private static readonly Module Media = M("migration-media", "/sitecore/media library/Project/A", "/sitecore/media library/Project/Shared");
    private static readonly Module Content = M("migration-content", "/sitecore/content/A/Home");
    private static readonly Module HospitalsMedia = M("migration-media-hospitals", "/sitecore/media library/Project/H");
    private static readonly Module HospitalsContent = M("migration-content-hospitals", "/sitecore/content/H/Home");
    private static readonly Module Roles = M("migration-roles");
    private static readonly Module Mixed = M("migration-mixed", "/sitecore/media library/Project/A", "/sitecore/content/A");
    private static readonly IReadOnlyList<Module> All = [Content, Media, HospitalsContent, HospitalsMedia, Roles, Mixed];

    private static IEnumerable<string> Stems(IEnumerable<Module> modules) => modules.Select(ContentScope.Stem);

    [Fact]
    public void A_media_module_has_only_media_library_includes()
    {
        Assert.True(ContentScope.IsMedia(Media));
        Assert.False(ContentScope.IsMedia(Content));
        Assert.False(ContentScope.IsMedia(Mixed));
        Assert.False(ContentScope.IsMedia(Roles));
    }

    [Fact]
    public void A_suffix_matches_a_content_module_and_its_media_twin_media_first()
    {
        Assert.Equal(["migration-media-hospitals", "migration-content-hospitals"], Stems(ContentScope.Match(All, "hospitals", skipMedia: false)));
        Assert.Equal(["migration-content-hospitals"], Stems(ContentScope.Match(All, "hospitals", skipMedia: true)));
    }

    [Theory]
    [InlineData("migration-content-hospitals")]
    [InlineData("migration-content-hospitals.module.json")]
    [InlineData("Project.migration-content-hospitals")]
    [InlineData("MIGRATION-CONTENT-HOSPITALS")]
    public void A_full_name_or_namespace_matches_one_module(string name) =>
        Assert.Equal(["migration-content-hospitals"], Stems(ContentScope.Match(All, name, skipMedia: false)));

    [Fact]
    public void An_exact_name_wins_over_suffixes()
    {
        var twin = M("other-migration-media", "/sitecore/media library/Project/O");
        Assert.Equal(["migration-media"], Stems(ContentScope.Match([.. All, twin], "migration-media", skipMedia: false)));
        Assert.Equal(["migration-media"], Stems(ContentScope.Match(All, "migration-media", skipMedia: false)));
        Assert.Equal(["migration-roles"], Stems(ContentScope.Match(All, "roles", skipMedia: false)));
    }

    [Fact]
    public void Other_multiple_matches_are_ambiguous()
    {
        var a = M("site-a-content", "/sitecore/content/A");
        var b = M("site-b-content", "/sitecore/content/B");
        var ex = Assert.Throws<CliException>(() => ContentScope.Match([a, b], "content", skipMedia: false));
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public void No_match_lists_the_modules()
    {
        var ex = Assert.Throws<CliException>(() => ContentScope.Match(All, "nothing", skipMedia: false));
        Assert.Contains("migration-content-hospitals.module.json", ex.Message);
    }

    [Fact]
    public void Skipping_media_from_a_media_module_leaves_nothing()
    {
        var ex = Assert.Throws<CliException>(() => ContentScope.Match(All, "migration-media-hospitals", skipMedia: true));
        Assert.Contains("--skip-media", ex.Message);
    }

    [Fact]
    public void Every_module_puts_media_first_and_can_leave_it_out()
    {
        Assert.Equal(["migration-media", "migration-media-hospitals", "migration-content", "migration-content-hospitals", "migration-roles", "migration-mixed"], Stems(ContentScope.All(All, skipMedia: false)));
        Assert.Equal(["migration-content", "migration-content-hospitals", "migration-roles", "migration-mixed"], Stems(ContentScope.All(All, skipMedia: true)));
        Assert.Throws<CliException>(() => ContentScope.All([Media], skipMedia: true));
    }

    [Fact]
    public void An_ad_hoc_module_writes_beside_the_other_item_files()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sitecore.json"), """{ "modules": [], "serialization": { "defaultMaxRelativeItemPathLength": 100 } }""");
        var root = ConfigurationLoader.LoadRoot(Path.Combine(_root, "sitecore.json"));
        var module = ConfigurationLoader.AdHocModule(root, "Tern.Content.Path", Path.Combine(_root, "items"), "adhoc", ItemPath.Parse("/sitecore/content/A/Home/page"), "master", TreeScope.ItemAndChildren);
        var include = Assert.Single(module.Includes);
        Assert.Same(module, include.Module);
        Assert.Equal(Path.Combine(_root, "items", "adhoc"), include.PhysicalRoot);
        Assert.Equal(100, include.MaxRelativePathLength);
        Assert.Equal(TreeScope.ItemAndChildren, include.Scope);
    }

    [Fact]
    public void Unregistered_module_files_beside_sitecore_json_are_candidates()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sitecore.json"), """{ "modules": ["registered.module.json"] }""");
        File.WriteAllText(Path.Combine(_root, "registered.module.json"), """{ "namespace": "Registered", "items": { "includes": [ { "name": "r", "path": "/sitecore/content/R" } ] } }""");
        File.WriteAllText(Path.Combine(_root, "extra.module.json"), """{ "namespace": "Extra", "items": { "includes": [ { "name": "e", "path": "/sitecore/content/E" } ] } }""");
        using var session = new Session(_root, new Output(json: false, verbose: false, trace: false));
        Assert.Equal(["Registered", "Extra"], ContentScope.Candidates(session).Select(m => m.Namespace));
        Assert.Equal(["Registered"], session.AllModules.Select(m => m.Namespace));
    }

    [Fact]
    public void Path_options_are_checked()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sitecore.json"), """{ "modules": [] }""");
        using var session = new Session(_root, new Output(json: false, verbose: false, trace: false));
        Assert.Throws<CliException>(() => ContentCommands.Resolve(session, new ContentRequest { Scope = "SingleItem" }));
        Assert.Throws<CliException>(() => ContentCommands.Resolve(session, new ContentRequest { Path = "/sitecore/content/A", Module = "x" }));
        Assert.Throws<CliException>(() => ContentCommands.Resolve(session, new ContentRequest { Path = "/sitecore/content/A", SkipMedia = true }));
        Assert.Throws<CliException>(() => ContentCommands.Resolve(session, new ContentRequest { Path = "/sitecore/content/A", Scope = "Everything" }));
        var (modules, _) = ContentCommands.Resolve(session, new ContentRequest { Path = "/sitecore/content/A", Scope = "singleitem", Database = "web" });
        var include = Assert.Single(Assert.Single(modules).Includes);
        Assert.Equal(TreeScope.SingleItem, include.Scope);
        Assert.Equal("web", include.Database);
    }

    [Fact]
    public async Task Gateway_timeouts_are_retried_and_other_failures_are_not()
    {
        ContentCommands.RetryDelay = TimeSpan.Zero;
        var output = new Output(json: false, verbose: false, trace: false) { Err = TextWriter.Null };
        var calls = 0;
        var exit = await ContentCommands.RetryAsync(output, "push", () =>
        {
            calls++;
            return calls < 3 ? throw new ManagementException("timeout", statusCode: 524) : Task.FromResult(0);
        }, CancellationToken.None);
        Assert.Equal((0, 3), (exit, calls));

        calls = 0;
        await Assert.ThrowsAsync<ManagementException>(() => ContentCommands.RetryAsync(output, "push", () =>
        {
            calls++;
            throw new ManagementException("bad", statusCode: (int)HttpStatusCode.BadRequest);
        }, CancellationToken.None));
        Assert.Equal(1, calls);

        calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => ContentCommands.RetryAsync(output, "push", () =>
        {
            calls++;
            throw new HttpRequestException("down");
        }, CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Step_documents_go_into_one_array()
    {
        var output = new Output(json: true, verbose: false, trace: false);
        using var buffer = new MemoryStream();
        using (var steps = new Utf8JsonWriter(buffer))
        {
            steps.WriteStartArray();
            output.Steps = steps;
            output.Document("ser pull", w => w.WriteNumber("changes", 2));
            output.Document("ser push", w => w.WriteNumber("changes", 1));
            output.Steps = null;
            steps.WriteEndArray();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        Assert.Equal(["ser pull", "ser push"], doc.RootElement.EnumerateArray().Select(e => e.GetProperty("command").GetString()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
