using Tern.Remote.Auth;

namespace Tern.Core.Tests.Remote;

/// <summary>Client credentials from <c>TERN_*</c> variables, with a project <c>.env</c> as the fallback.</summary>
public class EnvironmentCredentialsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tern-env-{Guid.NewGuid():N}");
    // Unique per test run, so a TERN_* variable on the machine can't match.
    private readonly string _endpoint = $"t{Guid.NewGuid():N}";

    public EnvironmentCredentialsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable(Key("CLIENT_ID"), null);
        Directory.Delete(_root, recursive: true);
    }

    private string Key(string name) => $"TERN_{_endpoint.ToUpperInvariant()}_{name}";

    [Fact]
    public void Parse_KeepsOnlyTernLines_AndStripsQuotesExportAndComments()
    {
        var values = DotEnv.Parse(
        [
            "# comment",
            "SITECORE_CLI_SECRET=other-tool",
            "TERN_CLIENT_ID=plain",
            "export TERN_CLIENT_SECRET=\"quoted # not a comment\"",
            "TERN_AUTHORITY='single'",
            "TERN_AUDIENCE=value # trailing comment",
            "TERN_EMPTY=",
            "not a pair",
        ]);

        Assert.Equal(4, values.Count);
        Assert.Equal("plain", values["TERN_CLIENT_ID"]);
        Assert.Equal("quoted # not a comment", values["TERN_CLIENT_SECRET"]);
        Assert.Equal("single", values["TERN_AUTHORITY"]);
        Assert.Equal("value", values["TERN_AUDIENCE"]);
    }

    [Fact]
    public void For_ReadsEndpointCredentials_FromTheProjectDotEnv()
    {
        File.WriteAllLines(Path.Combine(_root, ".env"), [$"{Key("CLIENT_ID")}=id-from-file", $"{Key("CLIENT_SECRET")}=secret-from-file"]);

        var credentials = EnvironmentCredentials.For(_endpoint, EnvironmentCredentials.Reader(_root));

        Assert.NotNull(credentials);
        Assert.Equal("id-from-file", credentials.ClientId);
        Assert.Equal("secret-from-file", credentials.ClientSecret);
    }

    [Fact]
    public void Reader_PrefersASetVariable_OverTheDotEnv()
    {
        File.WriteAllLines(Path.Combine(_root, ".env"), [$"{Key("CLIENT_ID")}=id-from-file"]);
        Environment.SetEnvironmentVariable(Key("CLIENT_ID"), "id-from-environment");

        Assert.Equal("id-from-environment", EnvironmentCredentials.Reader(_root)(Key("CLIENT_ID")));
    }

    [Fact]
    public void Reader_WithoutADotEnv_ReadsOnlyTheEnvironment()
    {
        Assert.Null(EnvironmentCredentials.Reader(_root)(Key("CLIENT_ID")));
        Assert.Null(EnvironmentCredentials.Reader(null)(Key("CLIENT_ID")));
    }
}
