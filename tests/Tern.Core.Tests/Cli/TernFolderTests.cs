using Tern.Cli;
using Tern.Cli.Commands;
using Tern.Cli.Common;

namespace Tern.Core.Tests.Cli;

/// <summary>
/// The repository's <c>.tern</c> folder: tern.json, .env and the content commands' Modules, found from
/// anywhere in the repository, and kept out of the <c>ser</c> commands.
/// </summary>
public class TernFolderTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"tern-folder-{Guid.NewGuid():N}");

    public TernFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Tern);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Directory.Delete(_repo, recursive: true);
    }

    private string Tern => Path.Combine(_repo, ".tern");

    private string Folder(params string[] parts)
    {
        var dir = Path.Combine([_repo, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private static Session SessionIn(string dir) => new(dir, new Output(false, false, false));

    private void ContentModule(string name, string path) =>
        Write(Path.Combine(Tern, name + ".module.json"), $$"""{ "namespace": "{{name}}", "items": { "includes": [ { "name": "{{name}}", "path": "{{path}}" } ] } }""");

    [Fact]
    public void TheTernFolder_IsFoundFromAnyFolderInTheRepository()
    {
        Assert.Equal(Tern, TernFolder.Find(Folder("src", "Project", "deep")));
        Assert.Equal(Tern, TernFolder.Find(_repo));
    }

    [Fact]
    public void TheSearch_StopsAtTheRepositoryRoot()
    {
        var nested = Folder("vendor", "other-repo");
        Directory.CreateDirectory(Path.Combine(nested, ".git"));

        Assert.Null(TernFolder.Find(Path.Combine(nested)));
    }

    [Fact]
    public void TernJsonInTheTernFolder_WinsOverTheOneBesideSitecoreJson()
    {
        var project = Folder("src");
        Write(Path.Combine(project, "tern.json"), "{}");
        Assert.Equal(Path.Combine(project, "tern.json"), TernFolder.ConfigFile(Tern, project));

        Write(Path.Combine(Tern, "tern.json"), "{}");
        Assert.Equal(Path.Combine(Tern, "tern.json"), TernFolder.ConfigFile(Tern, project));
    }

    [Fact]
    public void FromAFolderWithoutSitecoreJson_EndpointsAndCredentialsComeFromTheTernFolder()
    {
        Write(Path.Combine(Tern, "tern.json"), """{ "endpoints": { "prod": { "host": "https://prod.example", "allowWrite": false } } }""");
        Write(Path.Combine(Tern, ".env"), "TERN_PROD_CLIENT_ID=from-tern-folder");
        using var session = SessionIn(Folder("docs"));

        var prod = session.Endpoint("prod", announce: false);

        Assert.Equal("https://prod.example", prod.Host);
        Assert.False(prod.AllowWrite);
        Assert.Equal("from-tern-folder", session.EnvironmentVariables("TERN_PROD_CLIENT_ID"));
        Assert.Equal(Path.Combine(_repo, ".sitecore", "user.json"), session.StorePath);
    }

    [Fact]
    public void ContentCommands_UseTheTernFolderModules_WithItemFilesUnderTernItems()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """{ "modules": ["src/*.module.json"] }""");
        Write(Path.Combine(_repo, "src", "project.module.json"), """{ "namespace": "Project", "items": { "includes": [ { "name": "templates", "path": "/sitecore/templates/Project" } ] } }""");
        ContentModule("content-site-a", "/sitecore/content/A");
        ContentModule("media-site-a", "/sitecore/media library/A");
        using var session = SessionIn(Folder("src", "deep"));

        var (modules, description) = ContentCommands.Resolve(session, new ContentRequest { Module = "site-a" });

        Assert.Equal(["media-site-a", "content-site-a"], modules.Select(m => m.Namespace));
        Assert.Equal(Path.Combine(Tern, "items", "content-site-a"), modules[1].Includes[0].PhysicalRoot);
        Assert.Contains("content-site-a.module.json", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTernRoot_KeepsTheProjectsExcludedFields_ButNeverRemovesOrphanRolesOrUsers()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """
            { "modules": [], "serialization": { "removeOrphansForRoles": true, "removeOrphansForUsers": true, "defaultMaxRelativeItemPathLength": 90,
              "excludedFields": [ { "fieldId": "c7c26117-dbb1-42b2-ab5e-f7223845cca3", "description": "__Thumbnail" } ] } }
            """);
        using var session = SessionIn(_repo);

        session.UseTernFolder();

        Assert.False(session.Root.RemoveOrphansForRoles);
        Assert.False(session.Root.RemoveOrphansForUsers);
        Assert.Equal(90, session.Root.DefaultMaxRelativeItemPathLength);
        Assert.Single(session.Root.ExcludedFields);
    }

    [Fact]
    public void EveryContentModule_MeansEveryModuleInTheTernFolder()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """{ "modules": ["src/*.module.json"] }""");
        Write(Path.Combine(_repo, "src", "project.module.json"), """{ "namespace": "Project", "items": { "includes": [ { "name": "templates", "path": "/sitecore/templates/Project" } ] } }""");
        ContentModule("content-site-a", "/sitecore/content/A");
        using var session = SessionIn(_repo);

        var (modules, description) = ContentCommands.Resolve(session, new ContentRequest());

        Assert.Equal(["content-site-a"], modules.Select(m => m.Namespace));
        Assert.StartsWith("every module in .tern", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TernJsonModules_AreTheDefaultSet_AndTheRestCanStillBeNamed()
    {
        Write(Path.Combine(Tern, "tern.json"), """{ "modules": ["content-*.module.json"] }""");
        ContentModule("content-site-a", "/sitecore/content/A");
        ContentModule("templates-stage2", "/sitecore/templates/Project/Stage2");
        using var session = SessionIn(_repo);

        var (all, _) = ContentCommands.Resolve(session, new ContentRequest());
        var (named, _) = ContentCommands.Resolve(session, new ContentRequest { Module = "stage2" });

        Assert.Equal(["content-site-a"], all.Select(m => m.Namespace));
        Assert.Equal(["templates-stage2"], named.Select(m => m.Namespace));
    }

    [Fact]
    public void APathRun_WritesItsItemFilesUnderTheTernFolder()
    {
        using var session = SessionIn(Folder("anywhere"));

        var (modules, _) = ContentCommands.Resolve(session, new ContentRequest { Path = "/sitecore/content/A/Home" });

        Assert.Equal(Path.Combine(Tern, "items", "adhoc"), Assert.Single(modules).Includes[0].PhysicalRoot);
    }

    [Fact]
    public void WithoutTernModules_ContentCommandsStillUseSitecoreJson()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """{ "modules": ["*.module.json"] }""");
        Write(Path.Combine(_repo, "site.module.json"), """{ "namespace": "Site", "items": { "includes": [ { "name": "site", "path": "/sitecore/content/S" } ] } }""");
        using var session = SessionIn(_repo);

        var (modules, _) = ContentCommands.Resolve(session, new ContentRequest());

        Assert.Equal(["Site"], modules.Select(m => m.Namespace));
    }

    [Fact]
    public void SerializationModules_LeaveOutTheTernFolder_EvenWhenAGlobReachesIt()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """{ "modules": ["**/*.module.json"] }""");
        Write(Path.Combine(_repo, "src", "project.module.json"), """{ "namespace": "Project", "items": { "includes": [ { "name": "templates", "path": "/sitecore/templates/Project" } ] } }""");
        ContentModule("content-site-a", "/sitecore/content/A");
        using var session = SessionIn(_repo);

        Assert.Equal(["Project"], session.AllModules.Select(m => m.Namespace));
    }

    [Fact]
    public void SerCommands_AreRefusedInsideTheTernFolder_AndNameTheProjectToChangeTo()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """{ "modules": [] }""");
        var items = Folder(".tern", "items");

        var ex = Assert.Throws<CliException>(() => TernFolder.Guard(SessionIn(items), "ser push"));

        Assert.Contains("`tern ser push` is blocked here", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"cd \"{_repo}\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tern content sync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerCommands_RunAnywhereElse()
    {
        Write(Path.Combine(_repo, "sitecore.json"), """{ "modules": [] }""");

        TernFolder.Guard(SessionIn(_repo), "ser pull");
        TernFolder.Guard(SessionIn(Folder("src")), "ser pull");
    }
}
