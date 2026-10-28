using Tern.Cli;
using Tern.Cli.Common;
using Tern.Remote.Login;

namespace Tern.Core.Tests.Remote;

/// <summary>Endpoints from a committed <c>tern.json</c>, layered under <c>user.json</c> (ADR 0013).</summary>
public class SharedEndpointTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"tern-shared-{Guid.NewGuid():N}");

    public SharedEndpointTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Directory.Delete(_root, recursive: true);
    }

    private string UserJson => Path.Join(_root, ".sitecore", "user.json");

    private LoginStore Store(string userJson, params SharedEndpoint[] shared)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserJson)!);
        File.WriteAllText(UserJson, userJson);
        var store = LoginStore.Load(UserJson);
        store.Share(shared);
        return store;
    }

    [Fact]
    public void ASharedOnlyEndpoint_IsFoundAndListed_ButNotSaved()
    {
        var store = Store("""{ "endpoints": {} }""", new SharedEndpoint("prod", "https://prod.example", null, null, false, true));

        var prod = store.Find("PROD");
        store.Save();

        Assert.NotNull(prod);
        Assert.Equal("https://prod.example", prod.Host);
        Assert.False(prod.AllowWrite);
        Assert.True(prod.UseClientCredentials);
        Assert.Contains(store.All, e => e.Name == "prod" && e.Shared is not null);
        Assert.DoesNotContain("prod", File.ReadAllText(UserJson), StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedOnlyEndpoint_IsAddedToTheStore_OnItsFirstWrite()
    {
        var store = Store("""{ "endpoints": {} }""", new SharedEndpoint("dev", "https://dev.example", null, null, true, null));

        store.Find("dev")!.AccessToken = "token";
        store.Save();

        var saved = LoginStore.Load(UserJson).Find("dev");
        Assert.NotNull(saved);
        Assert.Equal("token", saved.AccessToken);
        Assert.Null(saved.Host);
    }

    [Fact]
    public void TernJsonWins_ForWhereTheEndpointIs_AndUserJsonKeepsTheTokens()
    {
        var store = Store(
            """{ "endpoints": { "dev": { "host": "https://old.example", "authority": "https://old-auth", "accessToken": "kept" } } }""",
            new SharedEndpoint("dev", "https://dev.example", "https://auth.example", null, null, null));

        var dev = store.Find("dev")!;

        Assert.Equal("https://dev.example", dev.Host);
        Assert.Equal("https://auth.example", dev.Authority);
        Assert.Equal("kept", dev.AccessToken);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, null, true)]
    [InlineData(null, true, true)]
    public void AllowWrite_IsTheStricterOfTheTwo(bool? shared, bool? local, bool expected)
    {
        var localJson = local is null ? "{}" : $$"""{ "allowWrite": {{(local.Value ? "true" : "false")}} }""";
        var store = Store($$"""{ "endpoints": { "uat": {{localJson}} } }""", new SharedEndpoint("uat", "https://uat.example", null, null, shared, null));

        Assert.Equal(expected, store.Find("uat")!.AllowWrite);
    }

    [Fact]
    public void ARefusal_NamesTernJson_WhenThatIsWhatMakesTheEndpointReadOnly()
    {
        var store = Store("""{ "endpoints": { "dev": { "allowWrite": false } } }""", new SharedEndpoint("prod", "https://prod.example", null, null, false, null));

        Assert.Contains("tern.json marks it read-only", ReadOnlyEndpoint.Message(store.Find("prod")!, "push to it"), StringComparison.Ordinal);
        Assert.Contains("--allow-write true to push to it", ReadOnlyEndpoint.Message(store.Find("dev")!, "push to it"), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReadsTheEndpointsOfTernJson()
    {
        File.WriteAllText(Path.Join(_root, "tern.json"), """
            {
              // Tern settings and endpoints share the file.
              "parallelRequests": 4,
              "endpoints": {
                "prod": { "host": "https://prod.example", "allowWrite": false, "useClientCredentials": true },
              }
            }
            """);

        var prod = Assert.Single(ProjectEndpoints.Load(Path.Join(_root, "tern.json")));

        Assert.Equal(new SharedEndpoint("prod", "https://prod.example", null, null, false, true), prod);
    }

    [Theory]
    [InlineData("clientSecret")]
    [InlineData("clientId")]
    [InlineData("accessToken")]
    public void Load_RefusesAnythingThatCouldHoldACredential(string property)
    {
        File.WriteAllText(Path.Join(_root, "tern.json"), $$"""{ "endpoints": { "dev": { "host": "https://dev.example", "{{property}}": "x" } } }""");

        var ex = Assert.Throws<CliException>(() => ProjectEndpoints.Load(Path.Join(_root, "tern.json")));

        Assert.Contains(property, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NeedsAHost()
    {
        File.WriteAllText(Path.Join(_root, "tern.json"), """{ "endpoints": { "dev": { "allowWrite": true } } }""");

        Assert.Throws<CliException>(() => ProjectEndpoints.Load(Path.Join(_root, "tern.json")));
    }

    [Fact]
    public void Load_WithoutTernJson_IsEmpty() => Assert.Empty(ProjectEndpoints.Load(Path.Join(_root, "tern.json")));
}
