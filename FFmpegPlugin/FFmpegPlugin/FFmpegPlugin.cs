using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using FFMpegCore;
using FFMpegCore.Extensions.SkiaSharp;
using FFmpegPlugin.Decode;
using Metasia.Core.Media;
using Metasia.Editor.Plugin;

namespace FFmpegPlugin;

public class FFmpegPlugin : IMediaInputPlugin, IDisposable
{
    public string PluginIdentifier { get; } = "SousiOmine.FFmpegPlugin";
    public string PluginVersion { get; } = "0.0.1";
    public string PluginName { get; } = "FFmpegInput&Output";

    private readonly FrameProvider _frameProvider = new();
    
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

    public async Task<VideoFileAccessorResult> GetBitmapAsync(string path, TimeSpan time)
    {
        try
        {
            Debug.WriteLine($"GetBitmapAsync呼び出し: {path}, {time}");

            // 非同期的にフレームを取得
            FrameItem frame = await _frameProvider.GetFrameAsync(path, time);
            var bitmap = frame.Bitmap;

            if (bitmap.Width > 0 && bitmap.Height > 0)
            {
                return new VideoFileAccessorResult() { IsSuccessful = true, Bitmap = bitmap };
            }
            else
            {
                return new VideoFileAccessorResult() { IsSuccessful = false };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetBitmapAsyncエラー: {ex.Message}");
            return new VideoFileAccessorResult() { IsSuccessful = false };
        }
    }

    public Task<ImageFileAccessorResult> GetBitmapAsync(string path)
    {
        throw new NotImplementedException();
    }
    public Task<VideoFileAccessorResult> GetBitmapAsync(string path, int frame)
    {
        throw new NotImplementedException();
    }
    
    public void Dispose()
    {
        _frameProvider?.Dispose();
        GC.SuppressFinalize(this);
    }
}