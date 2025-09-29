using System.Drawing;
using System.Reflection;
using FFMpegCore;
using FFMpegCore.Extensions.SkiaSharp;
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
        string? pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = Path.Combine(pluginDirectory!) });
        Console.WriteLine("Hello! from FFmpeg Plugin!");
    }

    public ImageFileAccessorResult GetBitmap(MediaPath path)
    {
        return new ImageFileAccessorResult() { IsSuccessful = false };
    }

    public VideoFileAccessorResult GetBitmap(MediaPath path, TimeSpan time, string? projectDir)
    {
        string targetPath = MediaPath.GetFullPath(path, projectDir);
        var bitmap = FFMpegImage.Snapshot(targetPath, captureTime:time);
        return new VideoFileAccessorResult() { IsSuccessful = true, Bitmap = bitmap };
    }

    public VideoFileAccessorResult GetBitmap(MediaPath path, int frame, string? projectDir)
    {
        string targetPath = MediaPath.GetFullPath(path, projectDir);
        //var bitmap = FFMpeg.Snapshot(input:targetPath, frame)
        return new VideoFileAccessorResult() { IsSuccessful = false };
    }
}