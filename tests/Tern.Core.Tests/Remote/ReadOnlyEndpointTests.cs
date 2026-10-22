using Tern.Cli;

namespace Tern.Core.Tests.Remote;

[CollectionDefinition(nameof(ConsoleTests), DisableParallelization = true)]
public class ConsoleTests;

/// <summary>Commands that can write refuse an Endpoint without <c>allowWrite</c> before they reach the CM.</summary>
[Collection(nameof(ConsoleTests))]
public class ReadOnlyEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tern-readonly-{Guid.NewGuid():N}");

    public ReadOnlyEndpointTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".sitecore"));
        // Port 1 refuses connections, so a command that gets past the check fails fast.
        File.WriteAllText(Path.Combine(_root, ".sitecore", "user.json"), """
            {
              "endpoints": {
                "default": { "host": "http://127.0.0.1:1", "allowWrite": false, "accessToken": "t", "expiresIn": 3600, "lastUpdated": "2099-01-01T00:00:00Z" }
              },
              "defaultEndpoint": "default"
            }
            """);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(int Code, string Error)> Run(params string[] args)
    {
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var code = await TernApp.RunAsync([.. args, "-c", _root]);
            return (code, error.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("publish item -p /sitecore/content/Home")]
    [InlineData("index rebuild")]
    [InlineData("index schema-populate -i sitecore_master_index")]
    [InlineData("dbcleanup")]
    [InlineData("script run -s Get-Item --spe-secret s")]
    [InlineData("authoring query -q mutation{deleteItem(input:{path:\"/sitecore/content/Home\"}){successful}}")]
    public async Task Refuses_a_write(string line)
    {
        var (code, error) = await Run(line.Split(' '));
        Assert.Equal(1, code);
        Assert.Contains("does not allow writes", error);
    }

    [Theory]
    [InlineData("authoring query -q {item(where:{path:\"/sitecore/content\"}){path}}")]
    [InlineData("index list")]
    [InlineData("index statistic")]
    [InlineData("publish list-targets")]
    public async Task Lets_a_read_through(string line)
    {
        var (code, error) = await Run(line.Split(' '));
        Assert.Equal(1, code);
        Assert.DoesNotContain("does not allow writes", error);
    }
}
