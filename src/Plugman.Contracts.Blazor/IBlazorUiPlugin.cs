using Microsoft.AspNetCore.Components;
using Plugman.Contracts;

namespace Plugman.Contracts.Blazor;

/// <summary>
/// Additive capability: this plugin ships a Razor component the host can render with
/// <c>&lt;DynamicComponent /&gt;</c>.
/// </summary>
/// <remarks>
/// The host must keep the plugin's component *inside* an existing render-mode boundary
/// (e.g. a globally interactive <c>&lt;Routes /&gt;</c>) rather than making the plugin
/// component itself the boundary: a boundary component's type is serialized by assembly
/// name for the circuit to re-resolve, which cannot work for a type that lives in a
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// </remarks>
public interface IBlazorUiPlugin : IPlugin
{
    /// <summary>Title for the section/tab the host renders around the component.</summary>
    string ViewTitle => Metadata.Name;

    /// <summary>The component type. Must implement <see cref="IComponent"/>.</summary>
    Type ComponentType { get; }

    /// <summary>Parameters passed to <c>DynamicComponent</c>. Keys must match component parameter names.</summary>
    IReadOnlyDictionary<string, object?>? DefaultParameters => null;
}
