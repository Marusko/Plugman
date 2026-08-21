namespace Plugman.Core;

/// <summary>
/// Builds the "load this from the host, never from the plugin folder" allow list.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important piece of the loader. Any assembly whose types cross the
/// host/plugin boundary must have exactly one <see cref="System.Type"/> identity in the
/// process. If <c>FrameworkElement</c> or <c>IComponent</c> gets loaded twice — once in the
/// default context for the host, once in the plugin's isolated context — every cast throws
/// <see cref="InvalidCastException"/> even though the type names are identical, because
/// type identity is (name, assembly, load context).
/// </para>
/// <para>
/// The list is derived from the manifest's <c>uiCapabilities</c> before the load context is
/// even constructed, so no part of the plugin has to be loaded to work out what it shares.
/// </para>
/// </remarks>
public static class SharedAssemblies
{
    /// <summary>
    /// Shared with every plugin regardless of capability: the contract surface itself, plus
    /// the logging abstractions the contract surface exposes through
    /// <see cref="Contracts.IPluginContext.Logger"/>.
    /// </summary>
    public static IReadOnlyList<string> Always { get; } =
    [
        "Plugman.Contracts",
        "Microsoft.Extensions.Logging.Abstractions"
    ];

    /// <summary>Assemblies shared with a plugin that declares <c>"wpf"</c>.</summary>
    public static IReadOnlyList<string> Wpf { get; } =
    [
        "Plugman.Contracts.Wpf",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml"
    ];

    /// <summary>Assemblies shared with a plugin that declares <c>"blazor"</c>.</summary>
    public static IReadOnlyList<string> Blazor { get; } =
    [
        "Plugman.Contracts.Blazor",
        "Microsoft.AspNetCore.Components",
        "Microsoft.AspNetCore.Components.Web"
    ];

    /// <summary>
    /// The assembly whose presence in the default load context proves the host can support a
    /// capability. Used to fail fast, from the manifest alone, when e.g. a Blazor plugin is
    /// dropped into a WPF-only host.
    /// </summary>
    public static string ProbeAssemblyFor(string capability) => capability.ToLowerInvariant() switch
    {
        PluginUiCapability.Wpf => "PresentationFramework",
        PluginUiCapability.Blazor => "Microsoft.AspNetCore.Components",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown UI capability.")
    };

    /// <summary>Assemblies shared for one capability.</summary>
    public static IReadOnlyList<string> For(string capability) => capability.ToLowerInvariant() switch
    {
        PluginUiCapability.Wpf => Wpf,
        PluginUiCapability.Blazor => Blazor,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown UI capability.")
    };

    /// <summary>
    /// Builds the full allow list for a plugin: the always-shared set, the set for every
    /// declared capability, and any extra assemblies the host wants to share (typically its
    /// own service contract assemblies, whose types plugins resolve from
    /// <see cref="Contracts.IPluginContext.Services"/>).
    /// </summary>
    public static HashSet<string> Build(
        IEnumerable<string>? uiCapabilities = null,
        IEnumerable<string>? additional = null)
    {
        var set = new HashSet<string>(Always, StringComparer.OrdinalIgnoreCase);

        foreach (var capability in uiCapabilities ?? [])
        {
            if (!PluginUiCapability.IsKnown(capability))
                throw new ArgumentOutOfRangeException(nameof(uiCapabilities), capability, "Unknown UI capability.");

            set.UnionWith(For(capability));
        }

        set.UnionWith(additional ?? []);
        return set;
    }
}
