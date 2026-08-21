using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Plugman.Contracts;

namespace SampleWpfPlugin;

/// <summary>The plugin's own MVVM. It never leaves the plugin.</summary>
public sealed class SampleViewModel : INotifyPropertyChanged
{
    private readonly IPluginContext _context;
    private int _ticks;
    private string _status = "Ready.";

    public SampleViewModel(IPluginContext context)
    {
        _context = context;
        TickCommand = new RelayCommand(Tick);
    }

    public string Title => "Sample WPF Plugin";

    public string DataDirectory => _context.PluginDataDirectory;

    public ICommand TickCommand { get; }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Tick()
    {
        _ticks++;
        Status = string.Create(CultureInfo.InvariantCulture, $"Ticked {_ticks} time(s) at {DateTime.Now:HH:mm:ss}.");
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
