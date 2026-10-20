using System.Net;
using Tern.Core.Configuration;
using Tern.Remote.Auth;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Cli.Common;

/// <summary>
/// What a command needs to run: <c>sitecore.json</c>, its Modules, the login store and, on demand,
/// an authenticated connection to an Endpoint's CM.
/// </summary>
public sealed class Session : IDisposable
{
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttp);
    private RootConfiguration? _root;
    private IReadOnlyList<Module>? _modules;
    private LoginStore? _store;

    public Session(string? configDirectory, Output output)
    {
        ConfigDirectory = configDirectory ?? Directory.GetCurrentDirectory();
        Output = output;
    }

    public string ConfigDirectory { get; }
    public Output Output { get; }
    public string? RecordDirectory { get; init; }
    public TernSettings Settings => _settings ??= TernSettings.Load(RootOrNull?.RootDirectory);
    private TernSettings? _settings;

    public static HttpClient Http => SharedHttp.Value;

    public RootConfiguration Root => _root ??= LoadRoot();

    /// <summary>The folder of <c>sitecore.json</c>, or the config folder when there is none.</summary>
    public string ProjectDirectory => RootOrNull?.RootDirectory ?? ConfigDirectory;

    private RootConfiguration? RootOrNull
    {
        get
        {
            try
            {
                return Root;
            }
            catch (CliException)
            {
                return null;
            }
        }
    }

    public IReadOnlyList<Module> AllModules => _modules ??= ConfigurationLoader.LoadModules(Root, Output.Warn);

    public IReadOnlyList<Module> SelectModules(IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var selected = ConfigurationLoader.Select(AllModules, include, exclude, Output.Warn);
        if (include.Count > 0 || exclude.Count > 0)
        {
            Output.Info("Matched module configurations: " + string.Join(", ", selected.Select(m => m.Namespace)));
        }
        return selected;
    }

    /// <summary>The union of every selected Module's excluded fields and the root's, as the Official CLI applies them.</summary>
    public IReadOnlyList<Guid> ExcludedFields(IReadOnlyList<Module> modules) =>
        Root.ExcludedFields.Concat(modules.SelectMany(m => m.ExcludedFields)).Select(f => f.FieldId).Distinct().ToList();

    public LoginStore Store => _store ??= LoginStore.Load(StorePath);

    /// <summary>Uses another session's login store, e.g. when installing a package from a temporary folder.</summary>
    public void UseStore(LoginStore store) => _store = store;

    public string StorePath => RootOrNull?.UserConfigurationPath ?? Path.Combine(ConfigDirectory, ".sitecore", "user.json");

    public Endpoint Endpoint(string? name)
    {
        var endpoint = Store.Find(name);
        if (endpoint is null)
        {
            var label = name ?? Store.DefaultEndpointName;
            throw new CliException($"There is no endpoint named '{label}' in {StorePath}. Run `tern login` or `tern connect` first.");
        }
        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new CliException($"Endpoint '{endpoint.Name}' has no host.");
        }
        return endpoint;
    }

    public TokenProvider Tokens(Endpoint endpoint)
    {
        var provider = new TokenProvider(endpoint, new OidcClient(Http), EnvironmentCredentials.For(endpoint.Name));
        provider.StoreChanged += () =>
        {
            lock (Store)
            {
                Store.Save();
            }
        };
        return provider;
    }

    public ManagementClient Management(Endpoint endpoint) =>
        new(Http, endpoint.Host!, Tokens(endpoint), RecordDirectory is null ? null : new RequestRecorder(RecordDirectory));

    public SerializationApi Serialization(Endpoint endpoint) =>
        new(Management(endpoint), new SerializationApiOptions
        {
            AliasesPerRequest = Settings.AliasesPerRequest,
            ParallelRequests = Settings.ParallelRequests,
        });

    private RootConfiguration LoadRoot()
    {
        var file = ConfigurationLoader.FindRootFile(ConfigDirectory)
            ?? throw new CliException($"No sitecore.json was found in {ConfigDirectory} or any folder above it.");
        return ConfigurationLoader.LoadRoot(file);
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public void Dispose()
    {
    }
}
