using Metasia.Editor.Plugin;

namespace FFmpegPlugin;

public sealed class FFmpegOutputSession : IMediaOutputSession
{
    public string Name => "FFmpeg Video";
    public string[] SupportedExtensions => ["*.mp4", "*.mkv", "*.mov", "*.avi"];
    public Avalonia.Controls.Control? SettingsView { get; }

    private readonly FFmpegOutputSettingsViewModel _viewModel;

    public FFmpegOutputSession(string pluginDirectory)
    {
        _viewModel = new FFmpegOutputSettingsViewModel();
        SettingsView = new FFmpegOutputSettingsView
        {
            DataContext = _viewModel
        };
        _pluginDirectory = pluginDirectory;
    }

    private readonly string _pluginDirectory;

    public Metasia.Core.Encode.EncoderBase CreateEncoderInstance()
    {
        return new FFmpegOutputEncoder(_pluginDirectory, _viewModel.CreateSettings());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
