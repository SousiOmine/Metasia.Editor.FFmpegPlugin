using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Metasia.Core.Encode;
using FFMpegCore;
using FFmpegPlugin.Decode;
using Metasia.Core.Media;
using Metasia.Core.Sounds;
using Metasia.Editor.Plugin;
using SkiaSharp;

namespace FFmpegPlugin;

public class FFmpegPlugin : IMediaInputPlugin, IMediaOutputPlugin, IDisposable
{
    public string PluginIdentifier { get; } = "SousiOmine.FFmpegPlugin";
    public string PluginVersion { get; } = "0.0.2";
    public string PluginName { get; } = "FFmpegInput&Output";
    public string Name { get; } = "FFmpeg MP4";
    public string[] SupportedExtensions { get; } = ["*.mp4"];

    private readonly FrameProvider _frameProvider = new();
    private string _pluginDirectory = AppContext.BaseDirectory;
    
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

            double startSeconds = Math.Max(0, (startTime ?? TimeSpan.Zero).TotalSeconds);
            double? durationSeconds = duration.HasValue && duration.Value > TimeSpan.Zero ? duration.Value.TotalSeconds : null;

            string startArg = startSeconds.ToString("F6", CultureInfo.InvariantCulture);
            string? durationArg = durationSeconds.HasValue ? durationSeconds.Value.ToString("F6", CultureInfo.InvariantCulture) : null;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startArg);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            if (durationArg is not null)
            {
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(durationArg);
            }
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("f64le");
            psi.ArgumentList.Add("-acodec");
            psi.ArgumentList.Add("pcm_f64le");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("pipe:1");

            using var process = new Process { StartInfo = psi };

            process.Start();

            using var output = new MemoryStream();
            Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"GetAudioAsync ffmpeg error: {stderrTask.Result}");
                return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
            }

            byte[] bytes = output.ToArray();
            int sampleCount = bytes.Length / sizeof(double);
            if (sampleCount <= 0)
            {
                return new AudioFileAccessorResult
                {
                    IsSuccessful = true,
                    Chunk = new AudioChunk(new AudioFormat(44100, 2), 0),
                };
            }

            double[] samples = new double[sampleCount];
            Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * sizeof(double));

            return new AudioFileAccessorResult
            {
                IsSuccessful = true,
                Chunk = new AudioChunk(new AudioFormat(44100, 2), samples),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetAudioAsyncエラー: {ex.Message}");
            return new AudioFileAccessorResult { IsSuccessful = false, Chunk = null };
        }
    }
    
    public void Dispose()
    {
        _frameProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    public EncoderBase CreateEncoderInstance()
    {
        return new FFmpegOutputEncoder(_pluginDirectory);
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
}
