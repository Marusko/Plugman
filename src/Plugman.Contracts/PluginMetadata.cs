namespace Plugman.Contracts;

/// <summary>
/// Immutable description of a plugin, supplied by the plugin itself.
/// </summary>
/// <param name="Id">Stable unique id, e.g. <c>"com.mycompany.sample"</c>. Must match the id in plugin.json.</param>
/// <param name="Name">Human readable display name.</param>
/// <param name="Version">Semver string.</param>
/// <param name="Description">Short description of what the plugin does.</param>
/// <param name="Author">Author or vendor name.</param>
/// <param name="Dependencies">Ids of other plugins that must be loaded before this one.</param>
/// <remarks>
/// This type lives in the shared contract assembly and holds nothing but strings, which is
/// what makes it safe for a host to keep a copy of a plugin's metadata after that plugin's
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> has been unloaded.
/// </remarks>
public sealed record PluginMetadata(
    string Id,
    string Name,
    string Version,
    string Description,
    string Author,
    string[]? Dependencies = null);
