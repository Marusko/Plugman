using Plugman.Contracts;

namespace Plugman.Core;

/// <summary>
/// An immutable snapshot of one discovered plugin. Safe to bind a UI to and safe to keep
/// after the plugin has been unloaded: it holds no types from the plugin's load context.
/// </summary>
/// <param name="Metadata">
/// The plugin's own metadata once loaded; before that, metadata synthesized from the manifest.
/// </param>
/// <param name="FolderPath">The plugin's folder under the plugins root.</param>
/// <param name="IsEnabled">Persisted enabled state, as last written to <c>plugin.json</c>.</param>
/// <param name="IsLoaded">Whether an instance is currently live in its own load context.</param>
/// <param name="LoadError">The last failure recorded for this plugin, or null.</param>
public sealed record PluginDescriptor(
    PluginMetadata Metadata,
    string FolderPath,
    bool IsEnabled,
    bool IsLoaded,
    PluginLoadError? LoadError)
{
    /// <summary>Convenience: the plugin id.</summary>
    public string Id => Metadata.Id;

    /// <summary>UI capabilities declared in the manifest, e.g. <c>["wpf"]</c>.</summary>
    public IReadOnlyList<string> UiCapabilities { get; init; } = [];

    /// <summary>True when the last operation on this plugin failed.</summary>
    public bool HasError => LoadError is not null;
}

/// <summary>Lifecycle transitions reported by <see cref="PluginManager.PluginStateChanged"/>.</summary>
public enum PluginState
{
    /// <summary>Found on disk by a scan.</summary>
    Discovered,

    /// <summary>Its folder disappeared and it was dropped from the registry.</summary>
    Removed,

    /// <summary>Loaded, activated and initialized.</summary>
    Loaded,

    /// <summary>Shut down and its load context unloaded.</summary>
    Unloaded,

    /// <summary>Enabled state persisted as true.</summary>
    Enabled,

    /// <summary>Enabled state persisted as false.</summary>
    Disabled,

    /// <summary>An operation failed; see <see cref="PluginDescriptor.LoadError"/>.</summary>
    Failed
}

/// <summary>Payload for <see cref="PluginManager.PluginStateChanged"/>.</summary>
public sealed class PluginStateChangedEventArgs(string pluginId, PluginState state, PluginDescriptor descriptor)
    : EventArgs
{
    public string PluginId { get; } = pluginId;
    public PluginState State { get; } = state;
    public PluginDescriptor Descriptor { get; } = descriptor;
}
