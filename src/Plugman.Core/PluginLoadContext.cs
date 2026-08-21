using System.Reflection;
using System.Runtime.Loader;

namespace Plugman.Core;

/// <summary>
/// A collectible load context for exactly one plugin folder.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order, and the order matters:
/// </para>
/// <list type="number">
/// <item>
/// Shared assembly? Return <c>null</c> so the runtime falls through to the default context
/// and the host's copy is used. This must come first — the plugin folder usually contains a
/// physical copy of the contract dlls, and loading those locally is exactly the mistake that
/// produces "InvalidCastException: cannot cast SamplePlugin to IPlugin".
/// </item>
/// <item>
/// Resolvable from the plugin's own <c>.deps.json</c>? Load it from the plugin folder. This
/// is what lets two plugins carry different versions of the same private dependency.
/// </item>
/// <item>
/// Otherwise <c>null</c>: framework assemblies and anything else fall through to the default
/// context, as they should.
/// </item>
/// </list>
/// </remarks>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblies;

    /// <param name="name">Diagnostic name, normally the plugin id.</param>
    /// <param name="mainAssemblyPath">Full path to the plugin's entry assembly.</param>
    /// <param name="sharedAssemblies">
    /// Simple assembly names that must resolve from the host. Build with <see cref="SharedAssemblies.Build"/>.
    /// </param>
    public PluginLoadContext(string name, string mainAssemblyPath, IEnumerable<string> sharedAssemblies)
        : base(name, isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainAssemblyPath);
        ArgumentNullException.ThrowIfNull(sharedAssemblies);

        MainAssemblyPath = mainAssemblyPath;
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _sharedAssemblies = new HashSet<string>(sharedAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Full path to the entry assembly this context was built around.</summary>
    public string MainAssemblyPath { get; }

    /// <summary>The allow list, for diagnostics and tests.</summary>
    public IReadOnlyCollection<string> SharedAssemblyNames => _sharedAssemblies;

    /// <summary>
    /// True when <paramref name="simpleName"/> must come from the host's default context.
    /// </summary>
    public bool IsShared(string? simpleName) =>
        simpleName is not null && _sharedAssemblies.Contains(simpleName);

    /// <summary>
    /// The path this context would load <paramref name="assemblyName"/> from, or null when it
    /// falls through to the default context. Exposed so the fall-through policy can be tested
    /// without actually loading anything.
    /// </summary>
    public string? ResolveAssemblyPath(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        return IsShared(assemblyName.Name) ? null : _resolver.ResolveAssemblyToPath(assemblyName);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared first, always. Never let the plugin folder win for a boundary-crossing type.
        if (IsShared(assemblyName.Name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
