using System.IO.Compression;
using System.Text.Json;
using Tern.Cli;
using Tern.Cli.Common;
using Tern.Remote.Cloud;

namespace Tern.Core.Tests.Cloud;

public class GitIgnoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tern-zip-{Guid.NewGuid():N}");

    public GitIgnoreTests()
    {
        // Keep the developer's own global ignore rules out of these tests.
        GitIgnore.GlobalExcludesFile = () => null;
    }

    private void Write(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Leaves_out_what_the_gitignore_files_ignore()
    {
        Write(".gitignore", """
            # build output
            bin/
            *.log
            !keep.log
            /root-only.txt
            docs/**/*.tmp
            node_modules
            \#literal
            trailing.txt
            """);
        Write("src/app.cs");
        Write("src/bin/out.dll");
        Write("bin/x.dll");
        Write("a.log");
        Write("src/keep.log");
        Write("root-only.txt");
        Write("src/root-only.txt");
        Write("docs/a/b/c.tmp");
        Write("docs/a/c.md");
        Write("web/node_modules/pkg/index.js");
        Write("#literal");
        Write("trailing.txt");
        Write("src/sub/.gitignore", "*.cs\n!main.cs\n");
        Write("src/sub/other.cs");
        Write("src/sub/main.cs");
        Write(".git/config");
        Write("sitecore.json");

        Assert.Equal(
        [
            ".gitignore",
            "docs/a/c.md",
            "sitecore.json",
            "src/app.cs",
            "src/keep.log",
            "src/root-only.txt",
            "src/sub/.gitignore",
            "src/sub/main.cs",
        ], GitIgnore.Files(_root));
    }

    [Fact]
    public void Applies_the_global_ignore_file_below_the_repository_rules()
    {
        Write("global-ignore", "*.local.json\nsecret.txt\n");
        Write(".gitignore", "!keep.local.json\n");
        Write("a.local.json");
        Write("keep.local.json");
        Write("secret.txt");
        GitIgnore.GlobalExcludesFile = () => Path.Combine(_root, "global-ignore");
        try
        {
            Assert.Equal([".gitignore", "global-ignore", "keep.local.json"], GitIgnore.Files(_root));
        }
        finally
        {
            GitIgnore.GlobalExcludesFile = () => null;
        }
    }

    [Fact]
    public void Reads_core_excludesFile_from_a_git_config() =>
        Assert.Equal("~/.gitignore_global", GitIgnore.CoreExcludesFile(["[user]", "  name = x", "[core]", "  autocrlf = true", "  excludesfile = \"~/.gitignore_global\""]));

    [Fact]
    public void Cannot_re_include_a_file_inside_an_ignored_folder()
    {
        Write(".gitignore", "build/\n!build/keep.txt\n");
        Write("build/keep.txt");
        Write("main.txt");
        Assert.Equal([".gitignore", "main.txt"], GitIgnore.Files(_root));
    }

    /// <summary>A git index with the given paths: version 2 (NUL-padded names) or 4 (prefix-compressed names).</summary>
    private static byte[] Index(int version, params string[] paths)
    {
        using var ms = new MemoryStream();
        void U32(uint v) => ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        ms.Write("DIRC"u8);
        U32((uint)version);
        U32((uint)paths.Length);
        var previous = string.Empty;
        foreach (var path in paths)
        {
            var start = ms.Position;
            ms.Write(new byte[60]);
            var name = System.Text.Encoding.UTF8.GetBytes(path);
            ms.Write([(byte)(name.Length >> 8 & 0x0f), (byte)name.Length]);
            if (version == 4)
            {
                var common = 0;
                while (common < previous.Length && common < path.Length && previous[common] == path[common])
                {
                    common++;
                }
                ms.WriteByte((byte)(previous.Length - common));
                ms.Write(System.Text.Encoding.UTF8.GetBytes(path[common..]));
                ms.WriteByte(0);
            }
            else
            {
                ms.Write(name);
                var length = ms.Position - start;
                ms.Write(new byte[((length + 8) & ~7) - length]);
            }
            previous = path;
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Reads_the_paths_of_a_git_index(int version) =>
        Assert.Equal(["a.txt", "lib/one.dll", "lib/two.dll", "src/deep/x.cs"], GitIndex.Read(Index(version, "a.txt", "lib/one.dll", "lib/two.dll", "src/deep/x.cs")));

    [Fact]
    public void Keeps_tracked_files_that_a_rule_ignores()
    {
        Write(".gitignore", "lib/\n*.dll\n");
        Write("a.txt");
        Write("lib/tracked.dll");
        Write("lib/untracked.dll");
        Write("b.dll");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllBytes(Path.Combine(_root, ".git", "index"), Index(2, ".gitignore", "a.txt", "lib/tracked.dll"));

        Assert.Equal([".gitignore", "a.txt", "lib/tracked.dll"], GitIgnore.Files(_root));
    }

    [Fact]
    public void Zips_the_files_with_forward_slash_paths()
    {
        Write(".gitignore", "*.tmp\n");
        Write("src/a.txt", "hello");
        Write("b.tmp");
        var result = SourceArchive.Create(_root);

        using var zip = new ZipArchive(new MemoryStream(result.Zip));
        Assert.Equal([".gitignore", "src/a.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var reader = new StreamReader(zip.GetEntry("src/a.txt")!.Open());
        Assert.Equal("hello", reader.ReadToEnd());
    }
}

public class CloudCommandLineTests
{
    [Theory]
    [InlineData("cloud login -aw --auth https://a/ --audience x --xmcloudhost https://d/ --xmcloudmonitoringhost https://m/ --client-id i --client-secret s --non-interactive")]
    [InlineData("cloud logout")]
    [InlineData("cloud project list --json")]
    [InlineData("cloud project create -n Shop")]
    [InlineData("cloud project create ProjectName Shop")]
    [InlineData("cloud project update -id p -n Shop")]
    [InlineData("cloud project delete -id p -f")]
    [InlineData("cloud project info --project-id p")]
    [InlineData("cloud environment list --project-id p")]
    [InlineData("cloud environment create --project-id p -n dev --prod --cm-only")]
    [InlineData("cloud environment update -id e -n dev")]
    [InlineData("cloud environment delete -id e --force")]
    [InlineData("cloud environment info -id e")]
    [InlineData("cloud environment connect -id e -aw -host cm.example.com")]
    [InlineData("cloud environment connect -id e --client-credentials --client-id i --client-secret s --auth https://a/ --audience x")]
    [InlineData("cloud environment disconnect -n dev")]
    [InlineData("cloud environment promote -id e --source-id d --no-watch --no-start --waitForPostActions")]
    [InlineData("cloud environment health -id e")]
    [InlineData("cloud environment restart -id e")]
    [InlineData("cloud environment variable list -id e")]
    [InlineData("cloud environment variable upsert -id e -n N -val v --target CM -s")]
    [InlineData("cloud environment variable delete -id e -n N")]
    [InlineData("cloud environment log list -id e --latest")]
    [InlineData("cloud environment log download -id e --log f.txt -o out -to 60")]
    [InlineData("cloud environment log view -id e --logfile f.txt")]
    [InlineData("cloud editinghost create -n eh -cm e")]
    [InlineData("cloud editinghost update -id e -n eh")]
    [InlineData("cloud editinghost delete -id e -f")]
    [InlineData("cloud editinghost deploy -id e --upload -dir . --no-watch --no-start -to 600")]
    [InlineData("cloud deployment info -id d")]
    [InlineData("cloud deployment list -id e")]
    [InlineData("cloud deployment create -id e --upload -dir . --no-watch --no-start -to 600 --waitForPostActions -dt 120")]
    [InlineData("cloud deployment start -id d --no-watch --waitForPostActions -dt 60")]
    [InlineData("cloud deployment watch -id d --waitForPostActions -dt 60")]
    [InlineData("cloud deployment cancel -id d")]
    [InlineData("cloud deployment log -id d --outputpath out")]
    [InlineData("cloud organization info --organization-id o")]
    [InlineData("cloud organization health")]
    [InlineData("cloud organization license -id o")]
    public void Accepts_the_Official_CLI_options(string line)
    {
        var parse = TernApp.Build().Parse(line.Split(' '));
        Assert.Empty(parse.Errors);
    }

    [Fact]
    public void Requires_the_IDs_the_Official_CLI_requires()
    {
        Assert.NotEmpty(TernApp.Build().Parse(["cloud", "environment", "list"]).Errors);
        Assert.NotEmpty(TernApp.Build().Parse(["cloud", "deployment", "info"]).Errors);
        // environment list has no -id alias in the Official CLI.
        Assert.NotEmpty(TernApp.Build().Parse(["cloud", "environment", "list", "-id", "p"]).Errors);
    }
}

public class CloudOutputTests
{
    [Fact]
    public void Prints_the_Official_CLI_payload_without_a_wrapper()
    {
        var projects = JsonDocument.Parse("""[{"id":"p1","name":"Shop","region":"aue","extra":1}]""").RootElement;
        using var stream = new MemoryStream();
        var output = new Output(json: true, verbose: false, trace: false) { JsonStream = stream };
        output.RawJson(w => CloudOutput.WriteArray(w, projects.EnumerateArray(), CloudOutput.Project));

        var root = JsonDocument.Parse(stream.ToArray()).RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        var project = Assert.Single(root.EnumerateArray());
        Assert.Equal(CloudOutput.Project.Select(f => f.JsonName), project.EnumerateObject().Select(p => p.Name));
        Assert.Equal("aue", project.GetProperty("region").GetString());
        Assert.Equal(0, project.GetProperty("sitecoreMajorVersion").GetInt32());
        Assert.False(project.TryGetProperty("schemaVersion", out _));
    }

    [Fact]
    public void Prints_null_for_a_command_without_a_result_as_the_Official_CLI_does()
    {
        using var stream = new MemoryStream();
        new Output(json: true, verbose: false, trace: false) { JsonStream = stream }.RawJson(w => w.WriteNullValue());
        Assert.Equal("null\n", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void Writes_deployment_statuses_as_numbers_and_shows_them_as_names()
    {
        var deployment = JsonDocument.Parse("""{"id":"d1","calculatedStatus":"Failed","buildStatus":2,"createdAt":"2023-09-13T01:06:52.3186832Z"}""").RootElement;
        var json = Tern.Remote.Cloud.CloudJson.Write(w =>
        {
            w.WritePropertyName("d");
            CloudOutput.WriteObject(w, deployment, CloudOutput.Deployment);
        });
        var d = JsonDocument.Parse(json).RootElement.GetProperty("d");

        Assert.Equal(3, d.GetProperty("calculatedStatus").GetInt32());
        Assert.Equal(2, d.GetProperty("buildStatus").GetInt32());
        Assert.Equal(0, d.GetProperty("provisioningStatus").GetInt32());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("postActionStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("startedAt").ValueKind);
        Assert.Equal("2023-09-13T01:06:52.3186832Z", d.GetProperty("createdAt").GetString());

        var rows = CloudOutput.Table(deployment, CloudOutput.Deployment).ToList();
        Assert.Contains("Calculated status                 : Failed", rows);
        Assert.Contains("Post Action status                : ", rows);
    }
}
