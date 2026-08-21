using Microsoft.Extensions.Logging;

namespace Plugman.Contracts;

/// <summary>
/// Everything a plugin is handed by the host. Created per plugin by the manager.
/// </summary>
public interface IPluginContext
{
    /// <summary>Logger scoped to this plugin; entries are attributed to the plugin id.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Host services, exposed read-only. Anything a plugin resolves from here must be typed
    /// against an assembly shared with the host (see the shared-assembly allow list in
    /// Plugman.Core), otherwise the cast fails across load contexts.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Per-plugin directory for plugin-owned state. Created by the manager before
    /// <see cref="IPlugin.InitializeAsync"/> runs.
    /// </summary>
    string PluginDataDirectory { get; }
}
