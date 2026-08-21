namespace Plugman.Contracts;

/// <summary>
/// Optional interactive console capability. It lives in the core contracts rather than in a
/// separate package because a console needs no UI framework reference.
/// </summary>
public interface IConsoleUiPlugin : IPlugin
{
    /// <summary>Title shown by the host when listing interactive plugins.</summary>
    string ScreenTitle { get; }

    /// <summary>Takes over the console until the user exits or <paramref name="ct"/> is cancelled.</summary>
    Task RunInteractiveAsync(IPluginContext context, CancellationToken ct);
}
