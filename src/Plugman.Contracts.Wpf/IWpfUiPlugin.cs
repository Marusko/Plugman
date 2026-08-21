using System.Windows;
using Plugman.Contracts;

namespace Plugman.Contracts.Wpf;

/// <summary>
/// Additive capability: this plugin can render itself into a WPF host.
/// </summary>
/// <remarks>
/// <para>
/// The plugin is fully self-contained. It builds its own view and view model inside
/// <see cref="CreateView"/>; the host only hosts the returned element (for example in a
/// <c>ContentControl</c>, <c>TabItem</c> or docking panel). No host-side DataTemplate
/// registration and no per-plugin host code.
/// </para>
/// <para>
/// Before unloading the plugin the host must remove the returned element from the visual
/// tree. A live <see cref="FrameworkElement"/> roots the plugin's load context and will
/// silently defeat the unload.
/// </para>
/// </remarks>
public interface IWpfUiPlugin : IPlugin
{
    /// <summary>Title for the tab/panel that hosts the view.</summary>
    string ViewTitle { get; }

    /// <summary>
    /// Builds a fresh view. Called on the UI thread. May be called more than once
    /// (e.g. after the host tears a panel down and reopens it), so it must not assume
    /// a single instance.
    /// </summary>
    FrameworkElement CreateView(IPluginContext context);
}
