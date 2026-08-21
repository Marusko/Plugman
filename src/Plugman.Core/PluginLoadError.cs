namespace Plugman.Core;

/// <summary>Where in a plugin's life the failure happened.</summary>
public enum PluginLoadStage
{
    /// <summary>Reading or validating <c>plugin.json</c>.</summary>
    Manifest,

    /// <summary>The host process cannot support a UI capability the manifest declares.</summary>
    HostCapability,

    /// <summary>A required dependency plugin is missing, disabled, cyclic or failed to load.</summary>
    Dependency,

    /// <summary>Loading the entry assembly into the plugin's load context.</summary>
    Assembly,

    /// <summary>Locating and constructing the entry type.</summary>
    Activation,

    /// <summary><see cref="Contracts.IPlugin.InitializeAsync"/> threw.</summary>
    Initialize,

    /// <summary>A capability call made through the manager threw.</summary>
    Capability,

    /// <summary><see cref="Contracts.IPlugin.ShutdownAsync"/> threw.</summary>
    Shutdown,

    /// <summary>Unloading failed or the load context stayed alive.</summary>
    Unload
}

/// <summary>
/// A plugin failure captured as plain data.
/// </summary>
/// <param name="Stage">Where the failure happened.</param>
/// <param name="Message">Human readable summary, safe to show in a UI.</param>
/// <param name="ExceptionType">Full name of the exception type, when the failure came from one.</param>
/// <param name="Details">Exception detail text (message chain plus stack trace), for logs.</param>
/// <remarks>
/// This record intentionally stores <em>strings only</em>. Holding the actual
/// <see cref="Exception"/> would root the plugin's <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// through the exception's stack trace and type, which silently defeats unloading — the
/// failure report would keep the failing plugin alive forever.
/// </remarks>
public sealed record PluginLoadError(
    PluginLoadStage Stage,
    string Message,
    string? ExceptionType = null,
    string? Details = null)
{
    /// <summary>Captures an exception without retaining any reference to it.</summary>
    public static PluginLoadError FromException(PluginLoadStage stage, string message, Exception ex)
    {
        var detail = ex.ToString();
        return new PluginLoadError(
            stage,
            $"{message} {ex.GetType().Name}: {ex.Message}",
            ex.GetType().FullName,
            detail);
    }

    public override string ToString() =>
        ExceptionType is null ? $"[{Stage}] {Message}" : $"[{Stage}] {Message} ({ExceptionType})";
}
