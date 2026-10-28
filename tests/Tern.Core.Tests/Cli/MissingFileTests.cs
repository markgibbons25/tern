using Tern.Cli;
using Tern.Core.Tests.Remote;

namespace Tern.Core.Tests.Cli;

/// <summary>A file named on the command line that doesn't exist fails with one line on stderr and exit code 1, not a stack trace.</summary>
[Collection(nameof(ConsoleTests))]
public class MissingFileTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"tern-missing-{Guid.NewGuid():N}");

    public MissingFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Directory.Delete(_root, recursive: true);
    }

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        var (originalOut, originalError) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var code = await TernApp.RunAsync([.. args, "-c", _root]);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData]
    [InlineData("--json")]
    public async Task Apply_with_a_missing_Plan(params string[] options)
    {
        var plan = Path.Join(_root, "missing.plan.json");
        var (code, output, error) = await Run(["apply", plan, .. options]);
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("missing.plan.json", error);
        Assert.DoesNotContain("FileNotFoundException", error);
        Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
