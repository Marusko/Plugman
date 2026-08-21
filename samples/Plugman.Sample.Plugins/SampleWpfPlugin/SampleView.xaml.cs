using System.Windows.Controls;

namespace SampleWpfPlugin;

/// <summary>
/// The plugin's view. Nothing about it is known to the host: no DataTemplate registration,
/// no ViewModel-first plumbing, no per-plugin host code.
/// </summary>
public partial class SampleView : UserControl
{
    public SampleView(SampleViewModel viewModel)
    {
        // InitializeComponent loads this control's compiled XAML through a pack URI, which
        // resolves the assembly by name. That only works for an assembly in a custom load
        // context while the plugin's context is the contextual reflection context — which is
        // what PluginManager establishes around every call into plugin code.
        InitializeComponent();
        DataContext = viewModel;
    }
}
