namespace Plugman.Core;

/// <summary>
/// Short names used in <c>plugin.json</c>'s <c>uiCapabilities</c> array. They match the
/// optional contract packages one-for-one.
/// </summary>
public static class PluginUiCapability
{
    /// <summary>Plugin implements <c>Plugman.Contracts.Wpf.IWpfUiPlugin</c>.</summary>
    public const string Wpf = "wpf";

    /// <summary>Plugin implements <c>Plugman.Contracts.Blazor.IBlazorUiPlugin</c>.</summary>
    public const string Blazor = "blazor";

    /// <summary>All capabilities Plugman knows how to validate and share assemblies for.</summary>
    public static IReadOnlyList<string> Known { get; } = [Wpf, Blazor];

    public static bool IsKnown(string capability) =>
        Known.Contains(capability, StringComparer.OrdinalIgnoreCase);
}
