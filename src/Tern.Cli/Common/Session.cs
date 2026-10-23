using System.Net;

using Tern.Core.Configuration;
using Tern.Core.Trees;
using Tern.Remote.Auth;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Cli.Common;

/// <summary>
/// What a command needs to run: <c>sitecore.json</c>, its Modules, the repository's <c>.tern</c> folder,
/// the login store and, on demand, an authenticated connection to an Endpoint's CM.
/// </summary>
public sealed class Session : IDisposable
{
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttp);
    private RootConfiguration? _root;
    private RootConfiguration? _projectRoot;
    private bool _projectRootLoaded;
    private string? _ternFolder;
    private bool _ternFolderFound;
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
    public TernSettings Settings => _settings ??= TernSettings.Load(ConfigFile);
    private TernSettings? _settings;

    public static HttpClient Http => SharedHttp.Value;

    /// <summary>The root commands work with: <c>sitecore.json</c>, or the <c>.tern</c> folder after <see cref="UseTernFolder"/>.</summary>
    public RootConfiguration Root => _root ??= ProjectRoot ?? throw new CliException($"No sitecore.json was found in {ConfigDirectory} or any folder above it.");

    /// <summary>The folder of <c>sitecore.json</c>, or the config folder when there is none.</summary>
    public string ProjectDirectory => ProjectRoot?.RootDirectory ?? ConfigDirectory;

    /// <summary>The repository's <c>.tern</c> folder, or null when it has none.</summary>
    public string? TernFolder
    {
        get
        {
            if (!_ternFolderFound)
            {
                _ternFolder = Common.TernFolder.Find(ConfigDirectory);
                _ternFolderFound = true;
            }
            return _ternFolder;
        }
    }

    /// <summary>The project's <c>tern.json</c>: in <c>.tern</c>, or else beside <c>sitecore.json</c>.</summary>
    public string? ConfigFile => Common.TernFolder.ConfigFile(TernFolder, ProjectRoot?.RootDirectory);

    /// <summary>Environment variables, with the <c>TERN_*</c> lines of <c>.tern/.env</c>, then the project folder's <c>.env</c>, as a fallback.</summary>
    public Func<string, string?> EnvironmentVariables => _environmentVariables ??= EnvironmentCredentials.Reader(TernFolder, ProjectDirectory);
    private Func<string, string?>? _environmentVariables;

    public bool HasSitecoreJson => ProjectRoot is not null;

    /// <summary>The <c>sitecore.json</c> at or above the config folder, or null when there is none.</summary>
    private RootConfiguration? ProjectRoot
    {
        get
        {
            if (!_projectRootLoaded)
            {
                _projectRoot = ConfigurationLoader.FindRootFile(ConfigDirectory) is { } file ? ConfigurationLoader.LoadRoot(file) : null;
                _projectRootLoaded = true;
            }
            return _projectRoot;
        }
    }

    /// <summary>
    /// Makes the <c>.tern</c> folder the root, for the <c>content</c> commands: its Modules, Item files under
    /// <c>.tern/items</c>. Logins, settings and <c>.env</c> stay where they were.
    /// </summary>
    public void UseTernFolder()
    {
        _root = Common.TernFolder.Root(TernFolder ?? throw new InvalidOperationException("There is no .tern folder."), ProjectRoot);
        _modules = null;
    }

    /// <summary>
    /// The root's Modules. The <c>.tern</c> folder's Modules are left out of <c>sitecore.json</c>'s, even where
    /// a glob such as <c>**/*.module.json</c> reaches them, so a <c>ser</c> command never touches them.
    /// </summary>
    public IReadOnlyList<Module> AllModules => _modules ??= LoadModules();

    private IReadOnlyList<Module> LoadModules()
    {
        var modules = ConfigurationLoader.LoadModules(Root, Output.Warn);
        if (TernFolder is not { } tern || Common.TernFolder.Contains(tern, Root.RootDirectory))
        {
            return modules;
        }
        var outside = modules.Where(m => !Common.TernFolder.Contains(tern, m.SourcePath)).ToList();
        if (outside.Count < modules.Count)
        {
            Output.Warn($"sitecore.json's modules globs match files in {tern}; they are for content commands, so serialization leaves them out. Narrow the globs to stop this warning.");
        }
        return outside;
    }

    public IReadOnlyList<Module> SelectModules(IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var selected = ConfigurationLoader.Select(AllModules, include, exclude, Output.Warn);
        if (include.Count > 0 || exclude.Count > 0)
        {
            Output.Info("Matched module configurations: " + string.Join(", ", selected.Select(m => Output.Paint(Output.ReadColor, m.Namespace))));
        }
        return selected;
    }

    /// <summary>The union of every selected Module's excluded fields and the root's, as the Official CLI applies them.</summary>
    public IReadOnlyList<Guid> ExcludedFields(IReadOnlyList<Module> modules) =>
        Root.ExcludedFields.Concat(modules.SelectMany(m => m.ExcludedFields)).Select(f => f.FieldId).Distinct().ToList();

    /// <summary>
    /// The user's Item file index for reading files with <paramref name="excluded"/> fields left
    /// out, or null when the <c>fileIndex</c> setting is off. Signatures depend on the excluded
    /// fields, so each set has its own index.
    /// </summary>
    public FileIndexCache? FileIndex(IEnumerable<Guid> excluded) =>
        Settings.FileIndex ? FileIndexCache.ForUser("excluded:" + string.Join(",", excluded.Select(g => g.ToString("N")).Order(StringComparer.Ordinal))) : null;

    public LoginStore Store => _store ??= LoadStore();

    private LoginStore LoadStore()
    {
        var store = LoginStore.Load(StorePath);
        store.Share(ProjectEndpoints.Load(ConfigFile));
        return store;
    }

    /// <summary>Uses another session's login store, e.g. when installing a package from a temporary folder.</summary>
    public void UseStore(LoginStore store) => _store = store;

    /// <summary>
    /// <c>.sitecore/user.json</c> beside <c>sitecore.json</c>; without one, at the repository root (the
    /// <c>.tern</c> folder's parent), or else in the config folder.
    /// </summary>
    public string StorePath => ProjectRoot?.UserConfigurationPath
        ?? Path.Combine(TernFolder is { } tern ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(tern))! : ConfigDirectory, ".sitecore", "user.json");

    /// <summary>
    /// The named Endpoint, or the default one. With <paramref name="announce"/>, the first time a
    /// command uses it, says which one it is and whether it is read-only, so a command never runs
    /// against the wrong environment unnoticed. Callers that name it themselves pass false.
    /// </summary>
    public Endpoint Endpoint(string? name, bool announce = true)
    {
        var endpoint = FindEndpoint(name);
        if (announce && Output.Announced.Add(endpoint.Name))
        {
            Output.Info($"Using {Output.PaintEndpoint(endpoint.Name, endpoint.Host)}{(endpoint.AllowWrite ? string.Empty : Output.Paint(Output.Dim, ", read-only"))}.");
        }
        return endpoint;
    }

    private Endpoint FindEndpoint(string? name)
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
        var provider = new TokenProvider(endpoint, new OidcClient(Http), EnvironmentCredentials.For(endpoint.Name, EnvironmentVariables));
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

    /// <summary>Plain REST calls to the Endpoint's CM, such as the transfer APIs.</summary>
    public Tern.Remote.Transfer.CmRestClient Rest(Endpoint endpoint) => new(Http, endpoint.Host!, Tokens(endpoint));

    public SerializationApi Serialization(Endpoint endpoint) =>
        new(Management(endpoint), new SerializationApiOptions
        {
            AliasesPerRequest = Settings.AliasesPerRequest,
            ParallelRequests = Settings.ParallelRequests,
            SplitThreshold = Settings.SplitThreshold ?? DefaultSplitThreshold(endpoint.Host!),
        });

    /// <summary>
    /// A remote CM reads a large subtree about 10% faster as parallel requests (measured on SitecoreAI);
    /// a local container gains nothing, so it gets one request per subtree.
    /// </summary>
    internal static int DefaultSplitThreshold(string host)
    {
        var name = Uri.TryCreate(host, UriKind.Absolute, out var uri) ? uri.Host : host;
        var local = name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(name.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return local ? 0 : 200;
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
