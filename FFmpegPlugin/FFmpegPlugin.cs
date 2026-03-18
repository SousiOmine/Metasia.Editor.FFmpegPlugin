using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using Metasia.Core.Encode;
using FFMpegCore;
using FFmpegPlugin.Decode;
using Metasia.Core.Media;
using Metasia.Core.Sounds;
using Metasia.Editor.Plugin;
using SkiaSharp;
using Avalonia.Controls;

namespace FFmpegPlugin;

public class FFmpegPlugin : IMediaInputPlugin, IMediaOutputPlugin, IPluginSettingsProvider, IDisposable
{
    public string PluginIdentifier { get; } = "SousiOmine.FFmpegPlugin";
    public string PluginVersion { get; } = "0.2.0";
    public string PluginName { get; } = "FFmpegInput&Output";
    public string Name { get; } = "FFmpeg MP4";
    public string[] SupportedExtensions { get; } = ["*.mp4"];
    public string SettingsDisplayName { get; } = "FFmpegPlugin 設定";

    private readonly FrameProvider _frameProvider = new();
    private readonly ConcurrentDictionary<string, AudioSession> _audioSessions = new(StringComparer.OrdinalIgnoreCase);
    private string _pluginDirectory = AppContext.BaseDirectory;
    private PluginSettings? _settings;
    
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
        _pluginDirectory = pluginDirectory;
        _settings = PluginSettings.Load(pluginDirectory);

        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = pluginDirectory });
        Environment.SetEnvironmentVariable(
            FFmpegPluginEnvironmentVariables.HardwareDecode,
            _settings.HardwareDecodeEnabled ? "1" : "0");
        Environment.SetEnvironmentVariable(
            FFmpegPluginEnvironmentVariables.HardwareDecodeApi,
            _settings.HardwareDecodeApi);

        Debug.WriteLine(
            $"FFmpeg Plugin initialized. settings={Path.Combine(pluginDirectory, PluginSettings.FileName)}, hwDecode={_settings.HardwareDecodeEnabled}, hwDecodeApi={_settings.HardwareDecodeApi}");
    }

    public Window CreateSettingsWindow()
    {
        _settings ??= PluginSettings.Load(_pluginDirectory);
        return new FFmpegSettingsWindow(_settings, _pluginDirectory);
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

    public async Task<AudioFileAccessorResult> GetAudioAsync(string path, TimeSpan? startTime = null, TimeSpan? duration = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
            }

            string ffmpegPath = ResolveFfmpegExecutablePath(_pluginDirectory);
            if (!File.Exists(ffmpegPath))
            {
                return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
            }

            AudioSession session = GetOrCreateAudioSession(path, ffmpegPath);
            AudioChunk? chunk = await session.GetAudioAsync(startTime, duration).ConfigureAwait(false);

            return new AudioFileAccessorResult
            {
                IsSuccessful = chunk is not null,
                Chunk = chunk,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetAudioAsyncエラー: {ex.Message}");
            return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
        }
    }
    
    public async Task<AudioSampleResult> GetAudioBySampleAsync(string path, long startSample, long sampleCount, int sampleRate)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AudioSampleResult { IsSuccessful = false, Chunk = null };
            }

            string ffmpegPath = ResolveFfmpegExecutablePath(_pluginDirectory);
            if (!File.Exists(ffmpegPath))
            {
                return new AudioSampleResult { IsSuccessful = false, Chunk = null };
            }

            AudioSession session = GetOrCreateAudioSession(path, ffmpegPath);
            AudioChunk? chunk = await session.GetAudioBySampleAsync(startSample, sampleCount, sampleRate).ConfigureAwait(false);

            return new AudioSampleResult
            {
                IsSuccessful = chunk is not null,
                Chunk = chunk,
                ActualStartSample = startSample,
                ActualSampleCount = (int)(chunk?.Length ?? 0),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetAudioBySampleAsyncエラー: {ex.Message}");
            return new AudioSampleResult { IsSuccessful = false, Chunk = null };
        }
    }
    
    public void Dispose()
    {
        _frameProvider.Dispose();
        DisposeAudioSessions();
        GC.SuppressFinalize(this);
    }

    public IMediaOutputSession CreateSession()
    {
        return new FFmpegOutputSession(_pluginDirectory);
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

    private static string ResolveFfmpegExecutablePath(string pluginDirectory)
    {
        string executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return Path.Combine(pluginDirectory, executableName);
    }

    private AudioSession GetOrCreateAudioSession(string mediaPath, string ffmpegPath)
    {
        return _audioSessions.GetOrAdd(mediaPath, _ => new AudioSession(ffmpegPath, mediaPath));
    }

    private void DisposeAudioSessions()
    {
        foreach (AudioSession session in _audioSessions.Values)
        {
            session.Dispose();
        }

        _audioSessions.Clear();
    }
}
