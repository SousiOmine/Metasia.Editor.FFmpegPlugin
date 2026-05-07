using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers;
using SkiaSharp;
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
    private const double FrameStageWeight = 0.6;
    private const double AudioStageWeight = 0.15;
    private const double MuxStageWeight = 0.25;

    private static readonly Regex ProgressTimeRegex = new(
        @"time=(\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string? _cachedHardwareEncoder;
    private bool _hardwareEncoderChecked;

    public string Name { get; } = "FFmpeg Video";
    public string[] SupportedExtensions { get; } = ["*.mp4", "*.mkv", "*.mov", "*.avi"];
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
        var encodeSw = Stopwatch.StartNew();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"metasia_ffmpeg_{Guid.NewGuid():N}");
        var tempVideoPath = Path.Combine(tempRoot, "video.mp4");
        var audioWavPath = Path.Combine(tempRoot, "audio.wav");

        try
        {
            Directory.CreateDirectory(tempRoot);

            var outputDirectory = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            Debug.WriteLine($"[FFmpeg] [Perf] [Encode-Start] frames={FrameCount} input={_projectWidth}x{_projectHeight} output={_outputWidth}x{_outputHeight} codec={_settings.VideoCodec} preset={_settings.VideoPreset} framerate={_projectFramerate}");

            var videoSw = Stopwatch.StartNew();
            await EncodeVideoOnlyAsync(tempVideoPath, cancellationToken).ConfigureAwait(false);
            videoSw.Stop();
            Debug.WriteLine($"[FFmpeg] [Perf] [Video-Only] time={videoSw.ElapsedMilliseconds}ms");

            var audioSw = Stopwatch.StartNew();
            await RenderAudioWavAsync(audioWavPath, cancellationToken).ConfigureAwait(false);
            audioSw.Stop();
            Debug.WriteLine($"[FFmpeg] [Perf] [Audio-Wav] time={audioSw.ElapsedMilliseconds}ms");

            var muxSw = Stopwatch.StartNew();
            await MuxVideoAndAudioAsync(tempVideoPath, audioWavPath, OutputPath, cancellationToken).ConfigureAwait(false);
            muxSw.Stop();
            Debug.WriteLine($"[FFmpeg] [Perf] [Mux] time={muxSw.ElapsedMilliseconds}ms");

            cancellationToken.ThrowIfCancellationRequested();
            var totalMs = encodeSw.ElapsedMilliseconds;
            Debug.WriteLine($"[FFmpeg] [Perf] [Encode-Completed] totalTime={totalMs}ms videoTime={videoSw.ElapsedMilliseconds}ms audioTime={audioSw.ElapsedMilliseconds}ms muxTime={muxSw.ElapsedMilliseconds}ms");
            ProgressRate = 1.0;
            Status = IEncoder.EncoderState.Completed;
            StatusChanged.Invoke(this, EventArgs.Empty);
            EncodeCompleted.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[FFmpeg] [Perf] [Encode-Canceled] elapsedTime={encodeSw.ElapsedMilliseconds}ms");
            Status = IEncoder.EncoderState.Canceled;
            StatusChanged.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] [Perf] [Encode-Failed] elapsedTime={encodeSw.ElapsedMilliseconds}ms error={ex.Message}");
            Debug.WriteLine($"FFmpeg出力失敗: {ex}");
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

        int frameByteCount = checked(_projectWidth * _projectHeight * 3 / 2);
        byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameByteCount);

        var index = 0;
        var frameSw = Stopwatch.StartNew();
        try
        {
            await foreach (var frame in GetFramesAsync(0, FrameCount - 1, cancellationToken).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (frame.PeekPixels() is { } pixmap)
                    {
                        using (pixmap)
                        {
                            RgbaPremulToYuv420p(pixmap.GetPixelSpan(), _projectWidth, _projectHeight, pixmap.RowBytes, frameBuffer);
                        }
                    }
                    else
                    {
                        using var fallback = SKBitmap.FromImage(frame);
                        RgbaPremulToYuv420p(fallback.GetPixelSpan(), _projectWidth, _projectHeight, fallback.RowBytes, frameBuffer);
                    }

                    await outputStream.WriteAsync(frameBuffer.AsMemory(0, frameByteCount), cancellationToken).ConfigureAwait(false);

                    index++;
                    var elapsedMs = frameSw.ElapsedMilliseconds;
                    if (index % 30 == 0 || index == FrameCount)
                    {
                        Debug.WriteLine($"[FFmpeg] [Perf] [Frame-Render] frame={index}/{FrameCount} elapsedMs={elapsedMs}ms avgMs={elapsedMs / (double)index:F2}ms fps={index / (elapsedMs / 1000.0):F1}");
                    }
                    SetProgressIfGreater(FrameStageWeight * (index / (double)FrameCount));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frameBuffer);
        }
    }

    private static void RgbaPremulToYuv420p(ReadOnlySpan<byte> rgba, int width, int height, int rowBytes, Span<byte> yuv)
    {
        int frameSize = width * height;
        int chromaWidth = width >> 1;
        int chromaHeight = height >> 1;
        int chromaSize = chromaWidth * chromaHeight;

        var yPlane = yuv[..frameSize];
        var uPlane = yuv.Slice(frameSize, chromaSize);
        var vPlane = yuv.Slice(frameSize + chromaSize, chromaSize);

        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int rSum = 0, gSum = 0, bSum = 0;

                for (int dy = 0; dy < 2; dy++)
                {
                    int py = y + dy;
                    int rowOffset = py * rowBytes;
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int px = x + dx;
                        if (px >= width) continue;

                        int offset = rowOffset + (px << 2);
                        byte r = rgba[offset];
                        byte g = rgba[offset + 1];
                        byte b = rgba[offset + 2];

                        int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                        yPlane[py * width + px] = (byte)((uint)yVal > 255 ? (yVal < 0 ? 0 : 255) : yVal);

                        rSum += r;
                        gSum += g;
                        bSum += b;
                    }
                }

                if (y + 1 >= height) continue;

                int avgR = rSum >> 2;
                int avgG = gSum >> 2;
                int avgB = bSum >> 2;

                int uVal = ((-38 * avgR - 74 * avgG + 112 * avgB + 128) >> 8) + 128;
                int vVal = ((112 * avgR - 94 * avgG - 18 * avgB + 128) >> 8) + 128;

                int chromaOffset = (y >> 1) * chromaWidth + (x >> 1);
                uPlane[chromaOffset] = (byte)((uint)uVal > 255 ? (uVal < 0 ? 0 : 255) : uVal);
                vPlane[chromaOffset] = (byte)((uint)vVal > 255 ? (vVal < 0 ? 0 : 255) : vVal);
            }
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
        var audioSw = Stopwatch.StartNew();
        var chunkIndex = 0;

        while (writtenSamples < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkSw = Stopwatch.StartNew();

            var chunkSampleCount = Math.Min(AudioChunkSampleCount, totalSamples - writtenSamples);
            var chunk = await GetAudioChunkAsync(
                writtenSamples,
                chunkSampleCount,
                AudioSampleRate,
                AudioChannelCount,
                cancellationToken).ConfigureAwait(false);

            var getAudioMs = chunkSw.ElapsedMilliseconds;

            var actualSampleCount = Math.Min(chunk.Length, chunkSampleCount);
            if (actualSampleCount <= 0)
            {
                throw new InvalidOperationException("音声サンプルの生成結果が空でした。");
            }

            var bytes = ToPcm16Bytes(chunk, actualSampleCount);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            writtenSamples += actualSampleCount;
            totalDataBytes += bytes.Length;
            chunkIndex++;

            if (chunkIndex % 5 == 0 || writtenSamples >= totalSamples)
            {
                Debug.WriteLine($"[FFmpeg] [Perf] [Audio-Chunk] chunk={chunkIndex} samples={writtenSamples}/{totalSamples} getAudioMs={getAudioMs}ms chunkTotalMs={chunkSw.ElapsedMilliseconds}ms");
            }

            var ratio = totalSamples <= 0 ? 1.0 : writtenSamples / (double)totalSamples;
            SetProgress(FrameStageWeight + (AudioStageWeight * ratio));
        }

        stream.Seek(0, SeekOrigin.Begin);
        WriteWavHeader(writer, totalDataBytes);
    }

    private async Task EncodeVideoOnlyAsync(string tempVideoPath, CancellationToken cancellationToken)
    {
        var ffmpegPath = FfmpegPathResolver.Resolve(_pluginDirectory);
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

        var containerFormat = GetContainerFormatFromPath(tempVideoPath);
        var effectiveSettings = _settings.ResolveForContainer(containerFormat);

        var totalDuration = TimeSpan.FromSeconds(FrameCount / _projectFramerate);
        var framerateArg = _projectFramerate.ToString("0.######", CultureInfo.InvariantCulture);
        var needScale = _projectWidth != _outputWidth || _projectHeight != _outputHeight;

        var videoCodec = effectiveSettings.VideoCodec;
        var videoPreset = effectiveSettings.VideoPreset;
        bool useHardware = effectiveSettings.UseHardwareEncoder
                        && videoCodec != "libaom-av1"
                        && containerFormat != FFmpegOutputContainer.Avi;

        if (useHardware)
        {
            var hwEncoder = await DetectHardwareEncoderAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
            if (hwEncoder is not null)
            {
                videoCodec = hwEncoder;
                videoPreset = MapPresetToHardware(videoPreset, hwEncoder);
                Debug.WriteLine($"[FFmpeg] [Perf] [HW-Encoder] detected={hwEncoder} preset={videoPreset}");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-stats_period");
        psi.ArgumentList.Add("0.05");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pixel_format");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-video_size");
        psi.ArgumentList.Add($"{_projectWidth}x{_projectHeight}");
        psi.ArgumentList.Add("-framerate");
        psi.ArgumentList.Add(framerateArg);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("pipe:0");

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(videoCodec);

        if (videoCodec is "libx264" or "libx265")
        {
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add(videoPreset);
        }
        else if (videoCodec is "libaom-av1")
        {
            psi.ArgumentList.Add("-cpu-used");
            psi.ArgumentList.Add("4");
        }
        else if (IsHardwareEncoder(videoCodec))
        {
            AddHardwareEncoderOptions(psi.ArgumentList, videoCodec, videoPreset);
        }

        if (needScale)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale={_outputWidth}:{_outputHeight}");
        }

        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");

        psi.ArgumentList.Add("-an");

        psi.ArgumentList.Add(tempVideoPath);

        using var process = new Process { StartInfo = psi };
        lock (_processLock)
        {
            _ffmpegProcess = process;
        }

        var stderr = new StringBuilder();
        var processStartSw = Stopwatch.StartNew();
        process.Start();
        processStartSw.Stop();
        Debug.WriteLine($"[FFmpeg] [Perf] [Video-ProcessStart] time={processStartSw.ElapsedMilliseconds}ms args=-f rawvideo -pixel_format yuv420p -video_size {_projectWidth}x{_projectHeight} -framerate {framerateArg} -c:v {videoCodec}");

        using var cancelRegistration = cancellationToken.Register(TryTerminateFfmpegProcess);
        var stderrTask = ConsumeFfmpegStderrAsync(process.StandardError, stderr, totalDuration, cancellationToken, progressBase: 0, progressRange: FrameStageWeight);
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
            throw new InvalidOperationException($"ffmpeg映像エンコード終了コード異常: {process.ExitCode}\n{stderr}");
        }

        ClearFfmpegProcessReference();
    }

    private static string MapPresetToHardware(string preset, string hwEncoder)
    {
        if (hwEncoder == "h264_nvenc")
        {
            return preset switch
            {
                "ultrafast" or "superfast" => "p1",
                "veryfast" or "faster" => "p2",
                "fast" or "medium" => "p3",
                "slow" => "p5",
                "slower" or "veryslow" => "p7",
                _ => "p2"
            };
        }

        if (hwEncoder == "h264_amf")
        {
            return preset switch
            {
                "ultrafast" or "superfast" or "veryfast" or "faster" => "speed",
                "slow" or "slower" or "veryslow" => "quality",
                _ => "balanced"
            };
        }

        if (hwEncoder == "h264_qsv")
        {
            return preset switch
            {
                "ultrafast" or "superfast" => "veryfast",
                "veryfast" => "veryfast",
                "faster" => "faster",
                "fast" => "fast",
                "slow" => "slow",
                "slower" => "slower",
                "veryslow" => "veryslow",
                _ => "medium"
            };
        }

        return preset;
    }

    private static bool IsHardwareEncoder(string videoCodec)
    {
        return videoCodec is "h264_nvenc" or "h264_amf" or "h264_qsv";
    }

    private static void AddHardwareEncoderOptions(System.Collections.ObjectModel.Collection<string> arguments, string videoCodec, string videoPreset)
    {
        arguments.Add("-preset");
        arguments.Add(videoPreset);

        if (videoCodec == "h264_nvenc")
        {
            arguments.Add("-rc");
            arguments.Add("vbr_hq");
        }
        else if (videoCodec == "h264_amf")
        {
            arguments.Add("-rc");
            arguments.Add("hqvbr");
        }

        arguments.Add("-b:v");
        arguments.Add("10M");
        arguments.Add("-maxrate");
        arguments.Add("20M");
    }

    private async Task<string?> DetectHardwareEncoderAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        if (_hardwareEncoderChecked)
        {
            return _cachedHardwareEncoder;
        }

        _hardwareEncoderChecked = true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-encoders");

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            foreach (var encoder in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
            {
                if (!output.Contains(encoder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await CanUseHardwareEncoderAsync(ffmpegPath, encoder, cancellationToken).ConfigureAwait(false))
                {
                    _cachedHardwareEncoder = encoder;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] [Perf] [HW-Detect-Failed] {ex.Message}");
        }

        return _cachedHardwareEncoder;
    }

    private static async Task<bool> CanUseHardwareEncoderAsync(string ffmpegPath, string encoder, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("color=c=black:s=32x32:r=1");
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(encoder);
        AddHardwareEncoderOptions(psi.ArgumentList, encoder, MapPresetToHardware("veryfast", encoder));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await outputTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return true;
            }

            Debug.WriteLine($"[FFmpeg] [Perf] [HW-Probe-Failed] encoder={encoder} exit={process.ExitCode} {stderr}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[FFmpeg] [Perf] [HW-Probe-Failed] encoder={encoder} {ex.Message}");
        }

        return false;
    }

    private async Task MuxVideoAndAudioAsync(string tempVideoPath, string audioWavPath, string outputPath, CancellationToken cancellationToken)
    {
        var ffmpegPath = FfmpegPathResolver.Resolve(_pluginDirectory);
        if (!File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException($"ffmpeg実行ファイルが見つかりません: {ffmpegPath}", ffmpegPath);
        }

        var containerFormat = GetContainerFormatFromPath(outputPath);
        var effectiveSettings = _settings.ResolveForContainer(containerFormat);
        LogEffectiveSettingsIfAdjusted(containerFormat, effectiveSettings);

        var totalDuration = TimeSpan.FromSeconds(FrameCount / _projectFramerate);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-stats_period");
        psi.ArgumentList.Add("0.05");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(tempVideoPath);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(audioWavPath);

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");

        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add(effectiveSettings.AudioCodec);
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add(effectiveSettings.AudioBitrate);

        if (effectiveSettings.EnableFastStart)
        {
            if (containerFormat == FFmpegOutputContainer.Mp4 || containerFormat == FFmpegOutputContainer.Mov)
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
        Debug.WriteLine($"[FFmpeg] [Perf] [Mux-Start] container={containerFormat} videoCodec=copy audioCodec={effectiveSettings.AudioCodec} audioBitrate={effectiveSettings.AudioBitrate} faststart={effectiveSettings.EnableFastStart}");

        using var cancelRegistration = cancellationToken.Register(TryTerminateFfmpegProcess);
        var progressBase = FrameStageWeight + AudioStageWeight;
        var progressRange = MuxStageWeight;
        await Task.WhenAll(
            ConsumeFfmpegStderrAsync(process.StandardError, stderr, totalDuration, cancellationToken, progressBase, progressRange),
            process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg多重化終了コード異常: {process.ExitCode}\n{stderr}");
        }
    }

    private async Task ConsumeFfmpegStderrAsync(
        StreamReader stderrReader,
        StringBuilder stderrBuffer,
        TimeSpan totalDuration,
        CancellationToken cancellationToken,
        double progressBase = 0,
        double progressRange = 1)
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
                SetProgressIfGreater(progressBase + (progressRange * ratio));
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

    private void ClearFfmpegProcessReference()
    {
        lock (_processLock)
        {
            _ffmpegProcess = null;
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

    private void LogEffectiveSettingsIfAdjusted(FFmpegOutputContainer container, FFmpegOutputSettings effectiveSettings)
    {
        if (container != FFmpegOutputContainer.Avi)
        {
            return;
        }

        if (_settings == effectiveSettings)
        {
            return;
        }

        Debug.WriteLine(
            $"AVI出力のためFFmpeg設定を補正しました。video={_settings.VideoCodec}->{effectiveSettings.VideoCodec}, audio={_settings.AudioCodec}->{effectiveSettings.AudioCodec}, faststart={_settings.EnableFastStart}->{effectiveSettings.EnableFastStart}");
    }

    private static FFmpegOutputContainer GetContainerFormatFromPath(string outputPath)
    {
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" => FFmpegOutputContainer.Mp4,
            ".mkv" => FFmpegOutputContainer.Mkv,
            ".mov" => FFmpegOutputContainer.Mov,
            ".avi" => FFmpegOutputContainer.Avi,
            _ => FFmpegOutputContainer.Mp4
        };
    }
}
