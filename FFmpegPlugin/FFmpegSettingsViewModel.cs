using System.Collections.ObjectModel;
using System.Reactive;
using Metasia.Editor.Plugin;
using ReactiveUI;

namespace FFmpegPlugin;

public class FFmpegSettingsViewModel : ReactiveObject
{
    private readonly PluginSettings _settings;
    private readonly string _pluginDirectory;
    private bool _hardwareDecodeEnabled;
    private string _selectedHwAccelApi;

    public bool HardwareDecodeEnabled
    {
        get => _hardwareDecodeEnabled;
        set => this.RaiseAndSetIfChanged(ref _hardwareDecodeEnabled, value);
    }

    public string SelectedHwAccelApi
    {
        get => _selectedHwAccelApi;
        set => this.RaiseAndSetIfChanged(ref _selectedHwAccelApi, value);
    }

    public ObservableCollection<string> HwAccelApis { get; } =
    [
        "auto",
        "d3d11va",
        "cuda",
        "qsv",
        "dxva2",
        "vaapi",
        "videotoolbox",
        "vdpau",
        "none"
    ];

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public event EventHandler? RequestClose;

    internal FFmpegSettingsViewModel(PluginSettings settings, string pluginDirectory)
    {
        _settings = settings;
        _pluginDirectory = pluginDirectory;

        _hardwareDecodeEnabled = settings.HardwareDecodeEnabled;
        _selectedHwAccelApi = settings.HardwareDecodeApi;

        SaveCommand = ReactiveCommand.Create(() =>
        {
            _settings.Decode.HardwareDecode = HardwareDecodeEnabled;
            _settings.Decode.HardwareDecodeApi = SelectedHwAccelApi;
            _settings.Save(_pluginDirectory);
            RequestClose?.Invoke(this, EventArgs.Empty);
        });

        CancelCommand = ReactiveCommand.Create(() =>
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        });
    }
}