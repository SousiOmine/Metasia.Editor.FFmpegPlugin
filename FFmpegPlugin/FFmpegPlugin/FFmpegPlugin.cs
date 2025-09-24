using System.Reflection;
using FFMpegCore;
using FFmpegPlugin.FFmpeg;
using Metasia.Core.Media;
using Metasia.Editor.Plugin;

namespace FFmpegPlugin;

public class FFmpegPlugin : IMediaInputPlugin
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
        string? pluginDirectory = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]);
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = pluginDirectory + "\\ffmpeg"});
        Console.WriteLine("Hello! from FFmpeg Plugin!");
    }

    public ImageFileAccessorResult GetBitmap(MediaPath path)
    {
        return new ImageFileAccessorResult() { IsSuccessful = false };
    }

    public VideoFileAccessorResult GetBitmap(MediaPath path, TimeSpan time, string? projectDir)
    {
        string targetPath = MediaPath.GetFullPath(path, "");
        //var bitmap = FFMpeg.Snapshot(input:targetPath, captureTime:(TimeSpan)time);
        return new VideoFileAccessorResult() { IsSuccessful = false };
    }

    public VideoFileAccessorResult GetBitmap(MediaPath path, int frame, string? projectDir)
    {
        return new VideoFileAccessorResult() { IsSuccessful = false };
    }
}