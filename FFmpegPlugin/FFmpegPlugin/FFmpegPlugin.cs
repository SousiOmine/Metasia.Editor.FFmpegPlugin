using System.Diagnostics;
using System.Reflection;
using FFMpegCore;
using FFmpegPlugin.Decode;
using Metasia.Core.Media;
using Metasia.Editor.Plugin;
using SkiaSharp;

namespace FFmpegPlugin;

public class FFmpegPlugin : IMediaInputPlugin, IDisposable
{
    public string PluginIdentifier { get; } = "SousiOmine.FFmpegPlugin";
    public string PluginVersion { get; } = "0.0.2";
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
        var pluginDirectory = ResolvePluginDirectory();
        var settings = PluginSettings.Load(pluginDirectory);

        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = pluginDirectory });
        Environment.SetEnvironmentVariable(
            FFmpegPluginEnvironmentVariables.HardwareDecode,
            settings.HardwareDecodeEnabled ? "1" : "0");
        Environment.SetEnvironmentVariable(
            FFmpegPluginEnvironmentVariables.HardwareDecodeApi,
            settings.HardwareDecodeApi);

        Debug.WriteLine(
            $"FFmpeg Plugin initialized. settings={Path.Combine(pluginDirectory, PluginSettings.FileName)}, hwDecode={settings.HardwareDecodeEnabled}, hwDecodeApi={settings.HardwareDecodeApi}");
    }

    public async Task<VideoFileAccessorResult> GetImageAsync(string path, TimeSpan time)
    {
        try
        {
            Debug.WriteLine($"GetImageAsync(TimeSpan)呼び出し: {path}, {time}");

            FrameItem frame = await _frameProvider.GetFrameAsync(path, time).ConfigureAwait(false);
            return CreateVideoResult(frame.Bitmap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetImageAsync(TimeSpan)エラー: {ex.Message}");
            return new VideoFileAccessorResult() { IsSuccessful = false };
        }
    }

    public async Task<ImageFileAccessorResult> GetImageAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ImageFileAccessorResult { IsSuccessful = false };
            }

            using var bitmap = await Task.Run(() => SKBitmap.Decode(path)).ConfigureAwait(false);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return new ImageFileAccessorResult { IsSuccessful = false };
            }

            var image = SKImage.FromBitmap(bitmap);
            return new ImageFileAccessorResult { IsSuccessful = true, Image = image };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetImageAsync(path)エラー: {ex.Message}");
            return new ImageFileAccessorResult { IsSuccessful = false };
        }
    }

    public async Task<VideoFileAccessorResult> GetImageAsync(string path, int frame)
    {
        try
        {
            Debug.WriteLine($"GetImageAsync(frame)呼び出し: {path}, {frame}");

            FrameItem frameItem = await _frameProvider.GetFrameAsync(path, frame).ConfigureAwait(false);
            return CreateVideoResult(frameItem.Bitmap);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetImageAsync(frame)エラー: {ex.Message}");
            return new VideoFileAccessorResult { IsSuccessful = false };
        }
    }
    
    public void Dispose()
    {
        _frameProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VideoFileAccessorResult CreateVideoResult(SKBitmap bitmap)
    {
        var image = SKImage.FromBitmap(bitmap);
        if (image.Width > 0 && image.Height > 0)
        {
            return new VideoFileAccessorResult { IsSuccessful = true, Image = image };
        }

        image.Dispose();
        return new VideoFileAccessorResult { IsSuccessful = false };
    }

    private static string ResolvePluginDirectory()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            var directory = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return AppContext.BaseDirectory;
    }
}
