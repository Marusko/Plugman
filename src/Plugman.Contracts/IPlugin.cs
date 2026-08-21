namespace Plugman.Contracts;

/// <summary>
/// The minimum every plugin implements. UI capabilities are additive interfaces
/// declared in optional packages (Plugman.Contracts.Wpf, Plugman.Contracts.Blazor).
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Must be cheap and side-effect free: the manager reads it immediately after
    /// activation, before <see cref="InitializeAsync"/>.
    /// </summary>
    PluginMetadata Metadata { get; }

    /// <summary>Called once after activation. Throwing here aborts the load and is reported as a load error.</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken ct);

    /// <summary>
    /// Called before the plugin's load context is unloaded. Release timers, file handles,
    /// event subscriptions and anything else that would keep the plugin alive.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct);
}
