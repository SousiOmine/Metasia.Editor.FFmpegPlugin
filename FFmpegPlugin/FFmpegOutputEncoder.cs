using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers;
using Metasia.Core.Encode;
using Metasia.Core.Media;
using Metasia.Core.Objects;
using Metasia.Core.Project;
using Metasia.Core.Sounds;

namespace FFmpegPlugin;

public sealed class FFmpegOutputEncoder : EncoderBase
{
    private const int AudioSampleRate = 48000;
    private const int AudioChannelCount = 2;
    private const int AudioBitsPerSample = 16;
    private const long AudioChunkSampleCount = AudioSampleRate * 5L;
    private const double FrameStageWeight = 0.7;
    private const double AudioStageWeight = 0.2;
    private const double MuxStageWeight = 0.1;

    private static readonly Regex ProgressTimeRegex = new(
        @"time=(\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name { get; } = "FFmpeg Video";
    public string[] SupportedExtensions { get; } = ["*.mp4", "*.mkv", "*.mov"];
    public override double ProgressRate { get; protected set; }

    public override event EventHandler<EventArgs> StatusChanged = delegate { };
    public override event EventHandler<EventArgs> EncodeStarted = delegate { };
    public override event EventHandler<EventArgs> EncodeCompleted = delegate { };
    public override event EventHandler<EventArgs> EncodeFailed = delegate { };

    public string OutputPath { get; private set; } = string.Empty;

    private readonly string _pluginDirectory;
    private readonly FFmpegOutputSettings _settings;
    private readonly object _processLock = new();
    private CancellationTokenSource _cts = new();
    private Task? _encodingTask;
    private Process? _ffmpegProcess;
    private double _projectFramerate = 30.0;
    private int _projectWidth;
    private int _projectHeight;
    private int _outputWidth;
    private int _outputHeight;

    public FFmpegOutputEncoder(string pluginDirectory, FFmpegOutputSettings? settings = null)
    {
        _pluginDirectory = string.IsNullOrWhiteSpace(pluginDirectory)
            ? AppContext.BaseDirectory
            : pluginDirectory;
        _settings = settings ?? FFmpegOutputSettings.Default;
    }

    public override void Initialize(
        MetasiaProject project,
        TimelineObject timeline,
        IImageFileAccessor imageFileAccessor,
        IVideoFileAccessor videoFileAccessor,
        IAudioFileAccessor audioFileAccessor,
        string projectPath,
        string outputPath)
    {
        base.Initialize(project, timeline, imageFileAccessor, videoFileAccessor, audioFileAccessor, projectPath, outputPath);
        OutputPath = outputPath;

        if (project.Info.Framerate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(project), "フレームレートは0より大きい必要があります。");
        }

        _projectFramerate = project.Info.Framerate;
        _projectWidth = (int)project.Info.Size.Width;
        _projectHeight = (int)project.Info.Size.Height;
        _outputWidth = _settings.OutputWidth ?? _projectWidth;
        _outputHeight = _settings.OutputHeight ?? _projectHeight;
    }

    public override void Start()
    {
        if (Status != IEncoder.EncoderState.Waiting)
        {
            throw new InvalidOperationException("エンコーダーが待機状態ではありません。");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new InvalidOperationException("出力先パスが指定されていません。");
        }

        _cts.Dispose();
        _cts = new CancellationTokenSource();

        Status = IEncoder.EncoderState.Encoding;
        ProgressRate = 0;
        StatusChanged.Invoke(this, EventArgs.Empty);
        EncodeStarted.Invoke(this, EventArgs.Empty);

        _encodingTask = Task.Run(() => EncodeAsync(_cts.Token));
        _encodingTask.ContinueWith(t =>
        {
            if (t.Exception is not null)
            {
                Debug.WriteLine($"FFmpegエンコードタスク失敗: {t.Exception}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public override void CancelRequest()
    {
        _cts.Cancel();
        TryTerminateFfmpegProcess();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            TryTerminateFfmpegProcess();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task EncodeAsync(CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"metasia_ffmpeg_{Guid.NewGuid():N}");
        var audioWavPath = Path.Combine(tempRoot, "audio.wav");

        try
        {
            Directory.CreateDirectory(tempRoot);

            var outputDirectory = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await RenderAudioWavAsync(audioWavPath, cancellationToken).ConfigureAwait(false);
            await MuxAsync(audioWavPath, OutputPath, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            ProgressRate = 1.0;
            Status = IEncoder.EncoderState.Completed;
            StatusChanged.Invoke(this, EventArgs.Empty);
            EncodeCompleted.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = IEncoder.EncoderState.Canceled;
            StatusChanged.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FFmpeg MP4出力失敗: {ex}");
            Status = IEncoder.EncoderState.Failed;
            StatusChanged.Invoke(this, EventArgs.Empty);
            EncodeFailed.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            TryTerminateFfmpegProcess();
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task WriteFramesToFfmpegAsync(Stream outputStream, CancellationToken cancellationToken)
    {
        if (FrameCount <= 0)
        {
            throw new InvalidOperationException("出力対象フレームがありません。");
        }

        if (_projectWidth <= 0 || _projectHeight <= 0)
        {
            throw new InvalidOperationException("プロジェクト解像度が不正です。");
        }

        int bytesPerPixel = 4;
        int rowByteCount = checked(_projectWidth * bytesPerPixel);
        int frameByteCount = checked(rowByteCount * _projectHeight);
        byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameByteCount);

        var index = 0;
        try
        {
            await foreach (var frame in GetFramesAsync(0, FrameCount - 1, cancellationToken).ConfigureAwait(false))
            {
                using (frame)
                using (var bitmap = SkiaSharp.SKBitmap.FromImage(frame))
                {
                    if (bitmap is null)
                    {
                        throw new InvalidOperationException("フレームをビットマップに変換できませんでした。");
                    }

                    if (bitmap.Width != _projectWidth || bitmap.Height != _projectHeight)
                    {
                        throw new InvalidOperationException($"フレーム解像度が一致しません。expected={_projectWidth}x{_projectHeight}, actual={bitmap.Width}x{bitmap.Height}");
                    }

                    var pixelBytes = bitmap.GetPixelSpan();
                    var sourceRowBytes = bitmap.RowBytes;
                    for (var y = 0; y < _projectHeight; y++)
                    {
                        var sourceOffset = y * sourceRowBytes;
                        var destinationOffset = y * rowByteCount;
                        pixelBytes.Slice(sourceOffset, rowByteCount).CopyTo(frameBuffer.AsSpan(destinationOffset, rowByteCount));
                    }
                }

                await outputStream.WriteAsync(frameBuffer.AsMemory(0, frameByteCount), cancellationToken).ConfigureAwait(false);

                index++;
                SetProgressIfGreater(AudioStageWeight + (FrameStageWeight * (index / (double)FrameCount)));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frameBuffer);
        }
    }

    private async Task RenderAudioWavAsync(string wavPath, CancellationToken cancellationToken)
    {
        var totalSamples = (long)Math.Ceiling((FrameCount / _projectFramerate) * AudioSampleRate);
        using var stream = File.Create(wavPath);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        WriteWavHeader(writer, dataByteLength: 0);

        long writtenSamples = 0;
        long totalDataBytes = 0;

        while (writtenSamples < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkSampleCount = Math.Min(AudioChunkSampleCount, totalSamples - writtenSamples);
            var chunk = await GetAudioChunkAsync(
                writtenSamples,
                chunkSampleCount,
                AudioSampleRate,
                AudioChannelCount,
                cancellationToken).ConfigureAwait(false);

            var actualSampleCount = Math.Min(chunk.Length, chunkSampleCount);
            if (actualSampleCount <= 0)
            {
                throw new InvalidOperationException("音声サンプルの生成結果が空でした。");
            }

            var bytes = ToPcm16Bytes(chunk, actualSampleCount);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            writtenSamples += actualSampleCount;
            totalDataBytes += bytes.Length;

            var ratio = totalSamples <= 0 ? 1.0 : writtenSamples / (double)totalSamples;
            SetProgress(FrameStageWeight + (AudioStageWeight * ratio));
        }

        stream.Seek(0, SeekOrigin.Begin);
        WriteWavHeader(writer, totalDataBytes);
    }

    private async Task MuxAsync(string audioWavPath, string outputPath, CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegExecutablePath(_pluginDirectory);
        if (!File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException($"ffmpeg実行ファイルが見つかりません: {ffmpegPath}", ffmpegPath);
        }

        if (_projectWidth <= 0 || _projectHeight <= 0)
        {
            throw new InvalidOperationException("プロジェクト解像度が不正です。");
        }

        if (_outputWidth <= 0 || _outputHeight <= 0)
        {
            throw new InvalidOperationException("出力解像度が不正です。");
        }

        var totalDuration = TimeSpan.FromSeconds(FrameCount / _projectFramerate);
        var framerateArg = _projectFramerate.ToString("0.######", CultureInfo.InvariantCulture);
        var needScale = _projectWidth != _outputWidth || _projectHeight != _outputHeight;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pixel_format");
        psi.ArgumentList.Add("bgra");
        psi.ArgumentList.Add("-video_size");
        psi.ArgumentList.Add($"{_projectWidth}x{_projectHeight}");
        psi.ArgumentList.Add("-framerate");
        psi.ArgumentList.Add(framerateArg);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("pipe:0");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(audioWavPath);

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(_settings.VideoCodec);

        if (_settings.VideoCodec == "libx264" || _settings.VideoCodec == "libx265")
        {
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add(_settings.VideoPreset);
        }

        if (_settings.VideoCodec == "libaom-av1")
        {
            psi.ArgumentList.Add("-cpu-used");
            psi.ArgumentList.Add("4");
        }

        if (needScale)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale={_outputWidth}:{_outputHeight}");
        }

        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");

        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add(_settings.AudioCodec);
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add(_settings.AudioBitrate);

        if (_settings.EnableFastStart)
        {
            var containerFormat = GetContainerFormatFromPath(outputPath);
            if (containerFormat == "mp4" || containerFormat == "mov")
            {
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
            }
        }

        psi.ArgumentList.Add("-shortest");
        psi.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = psi };
        lock (_processLock)
        {
            _ffmpegProcess = process;
        }

        var stderr = new StringBuilder();
        process.Start();

        using var cancelRegistration = cancellationToken.Register(TryTerminateFfmpegProcess);
        var stderrTask = ConsumeFfmpegStderrAsync(process.StandardError, stderr, totalDuration, cancellationToken);
        Task frameWriteTask = WriteFramesToFfmpegAsync(process.StandardInput.BaseStream, cancellationToken);

        try
        {
            await frameWriteTask.ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        await Task.WhenAll(stderrTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpegの終了コードが異常です: {process.ExitCode}\n{stderr}");
        }

        SetProgress(FrameStageWeight + AudioStageWeight + MuxStageWeight);
    }

    private async Task ConsumeFfmpegStderrAsync(
        StreamReader stderrReader,
        StringBuilder stderrBuffer,
        TimeSpan totalDuration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await stderrReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            stderrBuffer.AppendLine(line);
            if (TryParseFfmpegProgress(line, out var processed) && totalDuration > TimeSpan.Zero)
            {
                var ratio = Math.Clamp(processed.TotalSeconds / totalDuration.TotalSeconds, 0, 1);
                SetProgressIfGreater(AudioStageWeight + FrameStageWeight + (MuxStageWeight * ratio));
            }
        }
    }

    private static void WriteWavHeader(BinaryWriter writer, long dataByteLength)
    {
        var blockAlign = (short)(AudioChannelCount * (AudioBitsPerSample / 8));
        var byteRate = AudioSampleRate * blockAlign;
        var riffSize = checked((int)(36 + dataByteLength));
        var dataSize = checked((int)dataByteLength);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)AudioChannelCount);
        writer.Write(AudioSampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)AudioBitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
    }

    private static byte[] ToPcm16Bytes(IAudioChunk chunk, long sampleCount)
    {
        var channels = chunk.Format.ChannelCount;
        var frameCount = Math.Min(chunk.Length, sampleCount);
        var totalSamples = checked((int)(frameCount * channels));

        var result = new byte[totalSamples * sizeof(short)];
        for (var i = 0; i < totalSamples; i++)
        {
            var clamped = Math.Clamp(chunk.Samples[i], -1.0, 1.0);
            var pcm = (short)Math.Round(clamped * short.MaxValue);

            var offset = i * sizeof(short);
            result[offset] = (byte)(pcm & 0xFF);
            result[offset + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return result;
    }

    private void TryTerminateFfmpegProcess()
    {
        Process? process;
        lock (_processLock)
        {
            process = _ffmpegProcess;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            lock (_processLock)
            {
                if (ReferenceEquals(_ffmpegProcess, process))
                {
                    _ffmpegProcess = null;
                }
            }
        }
    }

    private void SetProgress(double value)
    {
        ProgressRate = Math.Clamp(value, 0, 1);
        StatusChanged.Invoke(this, EventArgs.Empty);
    }

    private void SetProgressIfGreater(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (value <= ProgressRate)
        {
            return;
        }

        ProgressRate = value;
        StatusChanged.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParseFfmpegProgress(string line, out TimeSpan processed)
    {
        var match = ProgressTimeRegex.Match(line);
        if (!match.Success)
        {
            processed = TimeSpan.Zero;
            return false;
        }

        return TimeSpan.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out processed);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"一時フォルダ削除失敗(IO): {path}, {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"一時フォルダ削除失敗(権限): {path}, {ex.Message}");
        }
    }

    private static string ResolveFfmpegExecutablePath(string pluginDirectory)
    {
        var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return Path.Combine(pluginDirectory, executableName);
    }

    private static string GetContainerFormatFromPath(string outputPath)
    {
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" => "mp4",
            ".mkv" => "mkv",
            ".mov" => "mov",
            _ => "mp4"
        };
    }
}
