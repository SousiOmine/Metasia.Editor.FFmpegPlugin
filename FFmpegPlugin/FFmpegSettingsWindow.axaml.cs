using Avalonia.Controls;
using Metasia.Editor.Plugin;

namespace FFmpegPlugin;

public partial class FFmpegSettingsWindow : Window
{
    internal FFmpegSettingsWindow(PluginSettings settings, string pluginDirectory)
    {
        InitializeComponent();
        var viewModel = new FFmpegSettingsViewModel(settings, pluginDirectory);
        viewModel.RequestClose += (_, _) => Close();
        DataContext = viewModel;
    }
}