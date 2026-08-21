namespace Plugman.Contracts;

/// <summary>
/// Non-UI capability: the plugin can execute named commands. Typical for console and
/// worker hosts, but usable from any host.
/// </summary>
public interface ICommandPlugin : IPlugin
{
    /// <summary>Commands this plugin answers to, for host-side discovery and help text.</summary>
    IReadOnlyList<string> Commands => [];

    Task<string> ExecuteAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken ct);
}
