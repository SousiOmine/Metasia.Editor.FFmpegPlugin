using Metasia.Editor.Plugin;

namespace Metasia.Editor.FFmpegPlugin;

public class FFmpegPlugin : IEditorPlugin
{
    public string PluginIdentifier { get; } = "SousiOmine.FFmpegPlugin";
    public string PluginVersion { get; } = "0.0.1";
    public string PluginName { get; } = "FFmpegInput&Output";
    public IEnumerable<IEditorPlugin.SupportEnvironment> SupportedEnvironments { get; } = new[]
    {
        IEditorPlugin.SupportEnvironment.Windows_arm64,
        IEditorPlugin.SupportEnvironment.Windows_x64,
        IEditorPlugin.SupportEnvironment.MacOS_arm64,
        IEditorPlugin.SupportEnvironment.MacOS_x64,
        IEditorPlugin.SupportEnvironment.Linux_arm64,
        IEditorPlugin.SupportEnvironment.Linux_x64,
    };
    
    public void Initialize()
    {
        Console.WriteLine("Hello! from FFmpeg Plugin!");
    }
}