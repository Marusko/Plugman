using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Plugman.Contracts;

namespace Plugman.Core;

/// <summary>
/// Discovers, loads, unloads and persists plugins found under a plugins root directory.
/// </summary>
/// <remarks>
/// <para>
/// Host-agnostic by construction: this type knows about <see cref="IPlugin"/> and nothing
/// else. A host asks for a capability with <see cref="GetPlugins{T}"/> — whether <c>T</c> is
/// a logic capability like <c>ICommandPlugin</c> or a UI capability like <c>IWpfUiPlugin</c>
/// makes no difference here, which is what keeps the same core working for console, WPF and
/// Blazor Server hosts.
/// </para>
/// <para>
/// Every call into plugin code is wrapped at this boundary. A plugin that throws while
/// initializing, while answering a command or while building its UI is logged, recorded on
/// its <see cref="PluginDescriptor.LoadError"/>, and otherwise ignored.
/// </para>
/// </remarks>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly string _pluginsRoot;
    private readonly string _dataRoot;
    private readonly IServiceProvider _hostServices;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginManager> _log;
    private readonly PluginManagerOptions _options;

    private readonly Dictionary<string, PluginRegistration> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<string>? _hostCapabilities;
    private bool _disposed;

    /// <param name="pluginsRootDirectory">Folder containing one subfolder per plugin.</param>
    /// <param name="hostServices">Services exposed to plugins through <see cref="IPluginContext.Services"/>.</param>
    /// <param name="loggerFactory">Used for the manager's own log and for each plugin's logger.</param>
    public PluginManager(string pluginsRootDirectory, IServiceProvider hostServices, ILoggerFactory loggerFactory)
        : this(pluginsRootDirectory, hostServices, loggerFactory, new PluginManagerOptions())
    {
    }

    public PluginManager(
        string pluginsRootDirectory,
        IServiceProvider hostServices,
        ILoggerFactory loggerFactory,
        PluginManagerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRootDirectory);
        ArgumentNullException.ThrowIfNull(hostServices);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);

        _pluginsRoot = Path.GetFullPath(pluginsRootDirectory);
        _hostServices = hostServices;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<PluginManager>();
        _options = options;
        _dataRoot = Path.GetFullPath(options.PluginDataRootDirectory ?? Path.Combine(_pluginsRoot, ".data"));
    }

    /// <summary>The folder being watched, fully qualified.</summary>
    public string PluginsRootDirectory => _pluginsRoot;

    /// <summary>UI capabilities this host can support, resolved once on first use.</summary>
    public IReadOnlyList<string> HostUiCapabilities =>
        _hostCapabilities ??= _options.HostUiCapabilities
            ?? PluginUiCapability.Known.Where(_options.HostCapabilityProbe.Supports).ToArray();

    /// <summary>Immutable snapshot of everything discovered so far.</summary>
    public IReadOnlyList<PluginDescriptor> DiscoveredPlugins
    {
        get
        {
            lock (_sync)
            {
                return _plugins.Values.Select(p => p.ToDescriptor()).ToArray();
            }
        }
    }

    /// <summary>Raised after every lifecycle transition. Handlers run outside the manager's lock.</summary>
    public event EventHandler<PluginStateChangedEventArgs>? PluginStateChanged;

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Rescans the plugins root. Safe to call at any time on a running host: new folders are
    /// picked up, folders that disappeared are dropped, and the enabled flag on disk wins.
    /// </summary>
    /// <remarks>
    /// Resilient by contract. A malformed manifest, an unreadable folder or a duplicate id is
    /// captured on that plugin's descriptor as a <see cref="PluginLoadError"/> and logged; it
    /// never aborts the scan for the other plugins.
    /// </remarks>
    public Task ScanAsync(CancellationToken ct = default) =>
        WithGateAsync(async (events, token) =>
        {
            Directory.CreateDirectory(_pluginsRoot);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in EnumeratePluginFolders())
            {
                token.ThrowIfCancellationRequested();
                ScanFolder(folder, seen, events);
            }

            DropVanishedPlugins(seen, events);

            if (_options.AutoLoadEnabledPlugins)
                await LoadEnabledCoreAsync(events, token).ConfigureAwait(false);

            return true;
        }, ct);

    private IEnumerable<string> EnumeratePluginFolders()
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(_pluginsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not enumerate plugins root {Root}.", _pluginsRoot);
            return [];
        }

        // '.'/'_' prefixed folders are infrastructure (the default .data root lives here), not plugins.
        return folders.Where(f =>
        {
            var name = Path.GetFileName(f);
            return name.Length > 0 && name[0] is not ('.' or '_');
        });
    }

    private void ScanFolder(string folder, HashSet<string> seen, List<PluginStateChangedEventArgs> events)
    {
        var manifestPath = Path.Combine(folder, PluginManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            _log.LogDebug("Skipping {Folder}: no {Manifest}.", folder, PluginManifest.FileName);
            return;
        }

        PluginManifest manifest;
        try
        {
            manifest = PluginManifest.Load(manifestPath);
        }
        catch (PluginManifestException ex)
        {
            // The id is exactly what a broken manifest fails to give us, so fall back to the
            // folder name. The plugin still shows up in the host's list, with its error.
            var fallbackId = Path.GetFileName(folder);
            _log.LogError(ex, "Malformed manifest in {Folder}.", folder);
            RegisterManifestFailure(fallbackId, folder, ex, seen, events);
            return;
        }

        if (!seen.Add(manifest.Id))
        {
            _log.LogError("Duplicate plugin id {Id} in {Folder}; ignoring this folder.", manifest.Id, folder);
            return;
        }

        lock (_sync)
        {
            if (_plugins.TryGetValue(manifest.Id, out var existing))
            {
                if (!PathsEqual(existing.FolderPath, folder) && existing.IsLoaded)
                {
                    _log.LogError(
                        "Plugin id {Id} in {Folder} clashes with the loaded plugin in {Existing}; ignoring.",
                        manifest.Id, folder, existing.FolderPath);
                    return;
                }

                // Disk is the source of truth for enabled state and descriptive fields.
                existing.Manifest = manifest;
                existing.FolderPath = folder;
                if (!existing.IsLoaded)
                {
                    existing.ResetMetadata();
                    existing.LoadError = null;
                }

                events.Add(new PluginStateChangedEventArgs(manifest.Id, PluginState.Discovered, existing.ToDescriptor()));
                return;
            }

            var registration = new PluginRegistration(manifest, folder);
            _plugins[manifest.Id] = registration;
            _log.LogInformation(
                "Discovered plugin {Id} ({Capabilities}) in {Folder}.",
                manifest.Id,
                manifest.NormalizedUiCapabilities.Count == 0 ? "logic only" : string.Join("+", manifest.NormalizedUiCapabilities),
                folder);

            events.Add(new PluginStateChangedEventArgs(manifest.Id, PluginState.Discovered, registration.ToDescriptor()));
        }
    }

    private void RegisterManifestFailure(
        string fallbackId,
        string folder,
        PluginManifestException ex,
        HashSet<string> seen,
        List<PluginStateChangedEventArgs> events)
    {
        if (!seen.Add(fallbackId))
            return;

        var error = PluginLoadError.FromException(PluginLoadStage.Manifest, "Manifest could not be read.", ex);

        lock (_sync)
        {
            if (_plugins.TryGetValue(fallbackId, out var existing) && existing.IsLoaded)
                return;

            var placeholder = new PluginRegistration(
                new PluginManifest { Id = fallbackId, EntryAssembly = string.Empty, Enabled = false },
                folder)
            {
                LoadError = error
            };

            _plugins[fallbackId] = placeholder;
            events.Add(new PluginStateChangedEventArgs(fallbackId, PluginState.Failed, placeholder.ToDescriptor()));
        }
    }

    private void DropVanishedPlugins(HashSet<string> seen, List<PluginStateChangedEventArgs> events)
    {
        List<PluginRegistration> gone;
        lock (_sync)
        {
            gone = _plugins.Values.Where(p => !seen.Contains(p.Id) && !p.IsLoaded).ToList();
            foreach (var registration in gone)
                _plugins.Remove(registration.Id);
        }

        foreach (var registration in gone)
        {
            _log.LogInformation("Plugin {Id} disappeared from disk; dropped from the registry.", registration.Id);
            events.Add(new PluginStateChangedEventArgs(registration.Id, PluginState.Removed, registration.ToDescriptor()));
        }
    }

    // ---------------------------------------------------------------- load / unload

    /// <summary>
    /// Loads, activates and initializes a plugin in its own collectible load context.
    /// </summary>
    /// <returns>
    /// The resulting descriptor. Failures are reported through
    /// <see cref="PluginDescriptor.LoadError"/> rather than thrown, so one bad plugin cannot
    /// take down a host's startup path.
    /// </returns>
    /// <exception cref="PluginNotFoundException">No plugin with this id was discovered.</exception>
    public Task<PluginDescriptor> LoadAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return WithGateAsync(
            (events, token) => LoadCoreAsync(Require(pluginId), events, [], token),
            ct);
    }

    /// <summary>Loads every discovered plugin whose manifest says it is enabled, in dependency order.</summary>
    public Task<IReadOnlyList<PluginDescriptor>> LoadEnabledAsync(CancellationToken ct = default) =>
        WithGateAsync(LoadEnabledCoreAsync, ct);

    private async Task<IReadOnlyList<PluginDescriptor>> LoadEnabledCoreAsync(
        List<PluginStateChangedEventArgs> events,
        CancellationToken ct)
    {
        List<PluginRegistration> candidates;
        lock (_sync)
        {
            candidates = _plugins.Values.Where(p => p.IsEnabled && !p.IsLoaded).ToList();
        }

        var results = new List<PluginDescriptor>(candidates.Count);
        foreach (var registration in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // A dependency may already have loaded this one on an earlier iteration.
            if (registration.IsLoaded)
            {
                results.Add(registration.ToDescriptor());
                continue;
            }

            results.Add(await LoadCoreAsync(registration, events, [], ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<PluginDescriptor> LoadCoreAsync(
        PluginRegistration registration,
        List<PluginStateChangedEventArgs> events,
        HashSet<string> loading,
        CancellationToken ct)
    {
        if (registration.IsLoaded)
            return registration.ToDescriptor();

        if (!loading.Add(registration.Id))
            return Fail(registration, new PluginLoadError(PluginLoadStage.Dependency, $"Dependency cycle involving '{registration.Id}'."), events);

        try
        {
            if (await ResolveDependenciesAsync(registration, events, loading, ct).ConfigureAwait(false) is { } dependencyError)
                return Fail(registration, dependencyError, events);

            if (CheckHostCapabilities(registration) is { } capabilityError)
                return Fail(registration, capabilityError, events);

            if (CreateInstance(registration) is { } activationError)
                return Fail(registration, activationError, events);

            if (await InitializeAsync(registration, ct).ConfigureAwait(false) is { } initError)
            {
                // Roll the load back: an uninitialized plugin must not stay half-alive.
                UnloadContext(registration);
                return Fail(registration, initError, events);
            }

            registration.LoadError = null;
            _log.LogInformation("Loaded plugin {Id} v{Version}.", registration.Id, registration.Metadata.Version);
            var descriptor = registration.ToDescriptor();
            events.Add(new PluginStateChangedEventArgs(registration.Id, PluginState.Loaded, descriptor));
            return descriptor;
        }
        finally
        {
            loading.Remove(registration.Id);
        }
    }

    private async Task<PluginLoadError?> ResolveDependenciesAsync(
        PluginRegistration registration,
        List<PluginStateChangedEventArgs> events,
        HashSet<string> loading,
        CancellationToken ct)
    {
        foreach (var dependencyId in registration.Dependencies)
        {
            PluginRegistration? dependency;
            lock (_sync)
            {
                _plugins.TryGetValue(dependencyId, out dependency);
            }

            if (dependency is null)
            {
                return new PluginLoadError(
                    PluginLoadStage.Dependency,
                    $"Requires plugin '{dependencyId}', which was not found under {_pluginsRoot}.");
            }

            if (dependency.IsLoaded)
                continue;

            if (loading.Contains(dependencyId))
            {
                return new PluginLoadError(
                    PluginLoadStage.Dependency,
                    $"Dependency cycle between '{registration.Id}' and '{dependencyId}'.");
            }

            var descriptor = await LoadCoreAsync(dependency, events, loading, ct).ConfigureAwait(false);
            if (!descriptor.IsLoaded)
            {
                return new PluginLoadError(
                    PluginLoadStage.Dependency,
                    $"Required plugin '{dependencyId}' failed to load: {descriptor.LoadError?.Message ?? "unknown error"}");
            }
        }

        return null;
    }

    /// <summary>
    /// Rejects, from the manifest alone, a plugin this host process cannot possibly render —
    /// e.g. a <c>"blazor"</c> plugin dropped into a WPF-only host. Doing it here turns what
    /// would be a confusing mid-scan TypeLoadException into a descriptive load error.
    /// </summary>
    private PluginLoadError? CheckHostCapabilities(PluginRegistration registration)
    {
        var declared = registration.Manifest.NormalizedUiCapabilities;
        if (declared.Count == 0)
            return null;

        var unsupported = declared.Where(c => !HostUiCapabilities.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unsupported.Length == 0)
            return null;

        var hostSupports = HostUiCapabilities.Count == 0 ? "none (logic plugins only)" : string.Join(", ", HostUiCapabilities);
        return new PluginLoadError(
            PluginLoadStage.HostCapability,
            $"Plugin '{registration.Id}' declares UI capability [{string.Join(", ", unsupported)}] " +
            $"which this host does not support. This host supports: {hostSupports}. " +
            "The plugin was not loaded; no assembly was touched.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PluginLoadError? CreateInstance(PluginRegistration registration)
    {
        var manifest = registration.Manifest;
        var assemblyPath = Path.Combine(registration.FolderPath, manifest.EntryAssembly);

        if (!File.Exists(assemblyPath))
        {
            return new PluginLoadError(
                PluginLoadStage.Assembly,
                $"Entry assembly '{manifest.EntryAssembly}' was not found in {registration.FolderPath}.");
        }

        var shared = SharedAssemblies.Build(manifest.NormalizedUiCapabilities, _options.AdditionalSharedAssemblies);
        var context = new PluginLoadContext(registration.Id, assemblyPath, shared);

        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            // Contextual reflection makes plain Assembly.Load calls made by plugin code (and by
            // frameworks acting on its behalf, notably WPF's pack-URI/BAML resolution) resolve
            // inside this context instead of failing against the default one.
            using var scope = context.EnterContextualReflection();

            if (FindEntryType(assembly, manifest, out var entryType) is { } typeError)
            {
                context.Unload();
                return typeError;
            }

            if (Activator.CreateInstance(entryType!) is not IPlugin instance)
            {
                context.Unload();
                return new PluginLoadError(
                    PluginLoadStage.Activation,
                    $"Type '{entryType!.FullName}' could not be created as an {nameof(IPlugin)}.");
            }

            registration.LoadContext = context;
            registration.Assembly = assembly;
            registration.Instance = instance;

            string? reportedId = null;
            try
            {
                var metadata = instance.Metadata;
                reportedId = new string(metadata.Id.AsSpan());
                registration.AdoptInstanceMetadata(metadata);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Plugin {Id} threw while reading Metadata; using manifest values.", registration.Id);
            }

            if (reportedId is not null && !string.Equals(reportedId, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Plugin in {Folder} reports id {ReportedId} but its manifest says {ManifestId}; the manifest wins.",
                    registration.FolderPath, reportedId, manifest.Id);
            }

            return null;
        }
        catch (Exception ex)
        {
            context.Unload();
            return PluginLoadError.FromException(
                PluginLoadStage.Assembly,
                $"Could not load '{manifest.EntryAssembly}'.",
                ex);
        }
    }

    private static PluginLoadError? FindEntryType(Assembly assembly, PluginManifest manifest, out Type? entryType)
    {
        entryType = null;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partially loadable assembly: keep going with whatever resolved.
            types = ex.Types.OfType<Type>().ToArray();
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            entryType = types.FirstOrDefault(t => string.Equals(t.FullName, manifest.EntryType, StringComparison.Ordinal));
            if (entryType is null)
            {
                return new PluginLoadError(
                    PluginLoadStage.Activation,
                    $"Entry type '{manifest.EntryType}' was not found in '{manifest.EntryAssembly}'.");
            }

            if (!typeof(IPlugin).IsAssignableFrom(entryType))
            {
                var error = DescribeNotAPlugin(entryType, manifest);
                entryType = null;
                return error;
            }

            return null;
        }

        var candidates = types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t))
            .ToArray();

        if (candidates.Length == 1)
        {
            entryType = candidates[0];
            return null;
        }

        if (candidates.Length == 0)
        {
            return new PluginLoadError(
                PluginLoadStage.Activation,
                $"'{manifest.EntryAssembly}' contains no public type implementing {nameof(IPlugin)}, " +
                "and the manifest does not name an entryType.");
        }

        return new PluginLoadError(
            PluginLoadStage.Activation,
            $"'{manifest.EntryAssembly}' contains {candidates.Length} {nameof(IPlugin)} implementations " +
            $"({string.Join(", ", candidates.Select(t => t.FullName))}). Name one in the manifest's entryType.");
    }

    /// <summary>
    /// Distinguishes "this type is simply not a plugin" from the classic duplicate-contract
    /// mistake, where the type <em>does</em> implement an <c>IPlugin</c> — just a different
    /// one, loaded a second time out of the plugin folder.
    /// </summary>
    private static PluginLoadError DescribeNotAPlugin(Type entryType, PluginManifest manifest)
    {
        var lookalike = entryType.GetInterfaces()
            .FirstOrDefault(i => string.Equals(i.FullName, typeof(IPlugin).FullName, StringComparison.Ordinal));

        if (lookalike is not null)
        {
            return new PluginLoadError(
                PluginLoadStage.Activation,
                $"Type '{entryType.FullName}' implements {typeof(IPlugin).FullName} from a second copy of " +
                $"'{lookalike.Assembly.GetName().Name}' loaded out of the plugin folder, so it is a different type " +
                "identity than the host's. Build the plugin with the contract references marked " +
                "Private=false / ExcludeAssets=runtime so the contract dlls are not copied next to it.");
        }

        return new PluginLoadError(
            PluginLoadStage.Activation,
            $"Entry type '{entryType.FullName}' in '{manifest.EntryAssembly}' does not implement {nameof(IPlugin)}.");
    }

    private async Task<PluginLoadError?> InitializeAsync(PluginRegistration registration, CancellationToken ct)
    {
        var instance = registration.Instance!;
        var context = registration.LoadContext!;

        string dataDirectory;
        try
        {
            dataDirectory = Path.Combine(_dataRoot, SanitizeForPath(registration.Id));
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception ex)
        {
            return PluginLoadError.FromException(
                PluginLoadStage.Initialize,
                "Could not create the plugin data directory.",
                ex);
        }

        var pluginContext = new PluginContext(
            _loggerFactory.CreateLogger($"Plugman.Plugin.{registration.Id}"),
            _hostServices,
            dataDirectory);

        registration.Context = pluginContext;

        try
        {
            using var scope = context.EnterContextualReflection();
            await instance.InitializeAsync(pluginContext, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Plugin {Id} threw from InitializeAsync.", registration.Id);
            return PluginLoadError.FromException(PluginLoadStage.Initialize, "InitializeAsync threw.", ex);
        }
    }

    /// <summary>
    /// Shuts a plugin down and unloads its load context. No-op when it is not loaded.
    /// </summary>
    /// <remarks>
    /// The unload only completes once the host has dropped every reference it holds into the
    /// plugin: instances from <see cref="GetPlugins{T}"/>, <c>Type</c> objects such as a cached
    /// <c>IBlazorUiPlugin.ComponentType</c>, and any WPF view still in a visual tree.
    /// </remarks>
    public Task UnloadAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return WithGateAsync(async (events, token) =>
        {
            var registration = Require(pluginId);
            await UnloadCoreAsync(registration, events, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    private async Task UnloadCoreAsync(
        PluginRegistration registration,
        List<PluginStateChangedEventArgs> events,
        CancellationToken ct)
    {
        if (!registration.IsLoaded)
            return;

        await ShutdownAsync(registration, ct).ConfigureAwait(false);
        UnloadContext(registration);

        _log.LogInformation("Unloaded plugin {Id}.", registration.Id);
        events.Add(new PluginStateChangedEventArgs(registration.Id, PluginState.Unloaded, registration.ToDescriptor()));
    }

    private async Task ShutdownAsync(PluginRegistration registration, CancellationToken ct)
    {
        var instance = registration.Instance;
        if (instance is null)
            return;

        try
        {
            using var scope = registration.LoadContext!.EnterContextualReflection();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.ShutdownTimeout);

            await instance.ShutdownAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let a bad shutdown block the unload; record it and carry on.
            _log.LogError(ex, "Plugin {Id} threw from ShutdownAsync; unloading anyway.", registration.Id);
            registration.LoadError = PluginLoadError.FromException(PluginLoadStage.Shutdown, "ShutdownAsync threw.", ex);
        }
    }

    /// <summary>
    /// Drops every reference into the plugin and unloads the context.
    /// </summary>
    /// <remarks>
    /// Marked <see cref="MethodImplOptions.NoInlining"/> on purpose: if the JIT inlined this
    /// into a caller whose frame stays alive, the local holding the plugin instance could keep
    /// the collectible loader allocator rooted and the unload would never complete.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UnloadContext(PluginRegistration registration)
    {
        var context = registration.LoadContext;

        registration.Instance = null;
        registration.Assembly = null;
        registration.Context = null;
        registration.LoadContext = null;
        registration.ResetMetadata();

        if (context is null)
            return;

        registration.UnloadProbe = new WeakReference(context, trackResurrection: true);
        context.Unload();
    }

    /// <summary>
    /// Development and test helper: forces collection until the plugin's load context is gone.
    /// </summary>
    /// <returns>
    /// True when the context was collected. False means something still holds a reference into
    /// the plugin — a cached instance, a <c>Type</c>, or a view still in a visual tree.
    /// </returns>
    public async Task<bool> WaitForUnloadAsync(string pluginId, TimeSpan timeout, CancellationToken ct = default)
    {
        var probe = Require(pluginId).UnloadProbe;
        if (probe is null)
            return true;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (!probe.IsAlive)
                return true;

            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        return !probe.IsAlive;
    }

    // ---------------------------------------------------------------- enable / disable

    /// <summary>Persists <c>enabled: true</c> and loads the plugin.</summary>
    public Task<PluginDescriptor> EnableAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return WithGateAsync(async (events, token) =>
        {
            var registration = Require(pluginId);

            if (PersistEnabled(registration, true) is { } persistError)
                return Fail(registration, persistError, events);

            events.Add(new PluginStateChangedEventArgs(registration.Id, PluginState.Enabled, registration.ToDescriptor()));
            return await LoadCoreAsync(registration, events, [], token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Unloads the plugin and persists <c>enabled: false</c>.</summary>
    public Task<PluginDescriptor> DisableAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return WithGateAsync(async (events, token) =>
        {
            var registration = Require(pluginId);

            await UnloadCoreAsync(registration, events, token).ConfigureAwait(false);

            if (PersistEnabled(registration, false) is { } persistError)
                return Fail(registration, persistError, events);

            var descriptor = registration.ToDescriptor();
            events.Add(new PluginStateChangedEventArgs(registration.Id, PluginState.Disabled, descriptor));
            return descriptor;
        }, ct);
    }

    private PluginLoadError? PersistEnabled(PluginRegistration registration, bool enabled)
    {
        try
        {
            PluginManifest.PersistEnabled(registration.ManifestPath, enabled);
            registration.Manifest.Enabled = enabled;
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not persist enabled={Enabled} for plugin {Id}.", enabled, registration.Id);
            return PluginLoadError.FromException(
                PluginLoadStage.Manifest,
                $"Could not persist enabled={enabled} to {PluginManifest.FileName}.",
                ex);
        }
    }

    // ---------------------------------------------------------------- consumption

    /// <summary>
    /// The loaded plugin with this id, if it is loaded and implements <typeparamref name="T"/>.
    /// </summary>
    public T? GetPlugin<T>(string pluginId) where T : class, IPlugin
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _plugins.TryGetValue(pluginId, out var registration) ? registration.Instance as T : null;
        }
    }

    /// <summary>
    /// Every loaded plugin implementing <typeparamref name="T"/> — the host's entry point for
    /// a capability, whether that is <c>ICommandPlugin</c> or <c>IWpfUiPlugin</c>.
    /// </summary>
    /// <remarks>
    /// The returned instances are live references into collectible load contexts. Do not cache
    /// them across an enable/disable cycle or the unload will not complete.
    /// </remarks>
    public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin
    {
        lock (_sync)
        {
            return _plugins.Values
                .Select(p => p.Instance)
                .OfType<T>()
                .ToArray();
        }
    }

    /// <summary>
    /// The context handed to a loaded plugin, or null when it is not loaded.
    /// </summary>
    /// <remarks>
    /// Capability methods that take an <see cref="IPluginContext"/> — <c>IWpfUiPlugin.CreateView</c>,
    /// <c>IConsoleUiPlugin.RunInteractiveAsync</c> — should be passed this, so the plugin sees
    /// the same logger and data directory it was initialized with.
    /// </remarks>
    public IPluginContext? GetPluginContext(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _plugins.TryGetValue(pluginId, out var registration) ? registration.Context : null;
        }
    }

    /// <summary>Descriptor for one plugin, or null when it was never discovered.</summary>
    public PluginDescriptor? GetDescriptor(string pluginId)
    {
        lock (_sync)
        {
            return _plugins.TryGetValue(pluginId, out var registration) ? registration.ToDescriptor() : null;
        }
    }

    /// <summary>
    /// Calls a plugin capability behind the manager's try/catch. Use it for anything that runs
    /// plugin code, including building a UI.
    /// </summary>
    public PluginInvocationResult<TResult> TryInvoke<TPlugin, TResult>(
        string pluginId,
        Func<TPlugin, TResult> call)
        where TPlugin : class, IPlugin
    {
        ArgumentNullException.ThrowIfNull(call);

        PluginRegistration? registration;
        lock (_sync)
        {
            _plugins.TryGetValue(pluginId, out registration);
        }

        if (registration?.Instance is not TPlugin plugin)
        {
            return PluginInvocationResult<TResult>.Fail(new PluginLoadError(
                PluginLoadStage.Capability,
                $"Plugin '{pluginId}' is not loaded or does not implement {typeof(TPlugin).Name}."));
        }

        try
        {
            using var scope = registration.LoadContext!.EnterContextualReflection();
            return PluginInvocationResult<TResult>.Ok(call(plugin));
        }
        catch (Exception ex)
        {
            var error = PluginLoadError.FromException(
                PluginLoadStage.Capability,
                $"{typeof(TPlugin).Name} call threw.",
                ex);

            _log.LogError(ex, "Plugin {Id} threw from a {Capability} call.", pluginId, typeof(TPlugin).Name);
            registration.LoadError = error;
            RaiseStateChanged(new PluginStateChangedEventArgs(pluginId, PluginState.Failed, registration.ToDescriptor()));
            return PluginInvocationResult<TResult>.Fail(error);
        }
    }

    /// <summary>Async counterpart of <see cref="TryInvoke{TPlugin, TResult}"/>.</summary>
    public async Task<PluginInvocationResult<TResult>> InvokeAsync<TPlugin, TResult>(
        string pluginId,
        Func<TPlugin, CancellationToken, Task<TResult>> call,
        CancellationToken ct = default)
        where TPlugin : class, IPlugin
    {
        ArgumentNullException.ThrowIfNull(call);

        PluginRegistration? registration;
        lock (_sync)
        {
            _plugins.TryGetValue(pluginId, out registration);
        }

        if (registration?.Instance is not TPlugin plugin)
        {
            return PluginInvocationResult<TResult>.Fail(new PluginLoadError(
                PluginLoadStage.Capability,
                $"Plugin '{pluginId}' is not loaded or does not implement {typeof(TPlugin).Name}."));
        }

        try
        {
            using var scope = registration.LoadContext!.EnterContextualReflection();
            var value = await call(plugin, ct).ConfigureAwait(false);
            return PluginInvocationResult<TResult>.Ok(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = PluginLoadError.FromException(
                PluginLoadStage.Capability,
                $"{typeof(TPlugin).Name} call threw.",
                ex);

            _log.LogError(ex, "Plugin {Id} threw from a {Capability} call.", pluginId, typeof(TPlugin).Name);
            registration.LoadError = error;
            RaiseStateChanged(new PluginStateChangedEventArgs(pluginId, PluginState.Failed, registration.ToDescriptor()));
            return PluginInvocationResult<TResult>.Fail(error);
        }
    }

    // ---------------------------------------------------------------- plumbing

    private PluginRegistration Require(string pluginId)
    {
        lock (_sync)
        {
            return _plugins.TryGetValue(pluginId, out var registration)
                ? registration
                : throw new PluginNotFoundException(pluginId);
        }
    }

    private PluginDescriptor Fail(
        PluginRegistration registration,
        PluginLoadError error,
        List<PluginStateChangedEventArgs> events)
    {
        registration.LoadError = error;
        _log.LogError("Plugin {Id} failed: {Error}", registration.Id, error);

        var descriptor = registration.ToDescriptor();
        events.Add(new PluginStateChangedEventArgs(registration.Id, PluginState.Failed, descriptor));
        return descriptor;
    }

    private async Task<T> WithGateAsync<T>(
        Func<List<PluginStateChangedEventArgs>, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var events = new List<PluginStateChangedEventArgs>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work(events, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();

            // Handlers run after the gate is released so a handler that calls back into the
            // manager (a UI refreshing itself, say) cannot deadlock.
            foreach (var e in events)
                RaiseStateChanged(e);
        }
    }

    private void RaiseStateChanged(PluginStateChangedEventArgs e)
    {
        try
        {
            PluginStateChanged?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "A PluginStateChanged handler threw for {Id}.", e.PluginId);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string SanitizeForPath(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Create(id.Length, id, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = Array.IndexOf(invalid, source[i]) >= 0 ? '_' : source[i];
        });
    }

    /// <summary>Shuts down and unloads every loaded plugin.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        List<PluginRegistration> loaded;
        lock (_sync)
        {
            loaded = _plugins.Values.Where(p => p.IsLoaded).ToList();
        }

        var events = new List<PluginStateChangedEventArgs>();
        foreach (var registration in loaded)
        {
            try
            {
                await UnloadCoreAsync(registration, events, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to unload plugin {Id} during dispose.", registration.Id);
            }
        }

        foreach (var e in events)
            RaiseStateChanged(e);

        _gate.Dispose();
    }
}
