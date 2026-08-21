using System.Reflection;
using System.Runtime.Loader;

namespace Plugman.Core;

/// <summary>
/// Answers "can this host process render a plugin that declares capability X?".
/// </summary>
public interface IHostCapabilityProbe
{
    bool Supports(string capability);
}

/// <summary>
/// Default probe: a capability is supported when its marker assembly resolves in the default
/// load context. In a console host <c>PresentationFramework</c> and
/// <c>Microsoft.AspNetCore.Components</c> both fail to resolve, so both UI capabilities are
/// reported unsupported — which is what turns "Blazor plugin in a WPF host" into a clean
/// <see cref="PluginLoadStage.HostCapability"/> error instead of a mid-scan
/// <see cref="TypeLoadException"/>.
/// </summary>
public sealed class DefaultHostCapabilityProbe : IHostCapabilityProbe
{
    private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();

    public bool Supports(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        lock (_sync)
        {
            if (_cache.TryGetValue(capability, out var cached))
                return cached;

            var supported = Probe(capability);
            _cache[capability] = supported;
            return supported;
        }
    }

    private static bool Probe(string capability)
    {
        if (!PluginUiCapability.IsKnown(capability))
            return false;

        var probeAssembly = SharedAssemblies.ProbeAssemblyFor(capability);

        // Already loaded is the common case in a running host, and avoids a resolution attempt.
        if (AssemblyLoadContext.Default.Assemblies.Any(
                a => string.Equals(a.GetName().Name, probeAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(probeAssembly));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>Optional knobs for <see cref="PluginManager"/>.</summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// Root for per-plugin data directories. Defaults to <c>.data</c> under the plugins root.
    /// Folders whose name starts with '.' or '_' are skipped by scanning, so the default
    /// location never looks like a plugin.
    /// </summary>
    public string? PluginDataRootDirectory { get; set; }

    /// <summary>
    /// UI capabilities this host supports. Leave null to auto-detect with
    /// <see cref="HostCapabilityProbe"/>; set it explicitly to make behaviour deterministic
    /// (tests do this, and so should a host that wants to refuse a capability it technically
    /// has the assemblies for).
    /// </summary>
    public IReadOnlyList<string>? HostUiCapabilities { get; set; }

    /// <summary>Probe used when <see cref="HostUiCapabilities"/> is null.</summary>
    public IHostCapabilityProbe HostCapabilityProbe { get; set; } = new DefaultHostCapabilityProbe();

    /// <summary>
    /// Extra simple assembly names every plugin must load from the host rather than from its
    /// own folder. Add the assemblies that define any host service contract a plugin resolves
    /// from <see cref="Contracts.IPluginContext.Services"/>.
    /// </summary>
    public IReadOnlyList<string> AdditionalSharedAssemblies { get; set; } = [];

    /// <summary>Load every enabled plugin at the end of <see cref="PluginManager.ScanAsync"/>. Default false.</summary>
    public bool AutoLoadEnabledPlugins { get; set; }

    /// <summary>
    /// How long <see cref="Contracts.IPlugin.InitializeAsync"/> gets before the manager gives up
    /// on it. The call is made while the manager's lock is held, so without a timeout a plugin
    /// that never returns would deadlock every later operation on the manager.
    /// </summary>
    public TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="Contracts.IPlugin.ShutdownAsync"/> gets before the manager stops
    /// waiting and unloads anyway. A plugin that hangs its shutdown must not hang the host.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
