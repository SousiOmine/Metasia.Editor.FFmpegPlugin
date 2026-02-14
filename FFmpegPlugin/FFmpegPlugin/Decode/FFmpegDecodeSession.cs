using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using Channel = System.Threading.Channels.Channel;

namespace FFmpegPlugin.Decode;

/// <summary>
/// 1つの動画ファイルごとにデコード処理を担当する
/// </summary>
public class FFmpegDecodeSession : IDisposable
{
    private const int MinPipeBlockSize = 256 * 1024;
    private const int MaxPipeBlockSize = 8 * 1024 * 1024;
    private const int DecodeChannelCapacity = 8;
    private static readonly HashSet<string> SupportedHwAccelApis = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto",
        "none",
        "vdpau",
        "dxva2",
        "d3d11va",
        "vaapi",
        "qsv",
        "videotoolbox",
        "cuda"
    };
    private readonly string _videoPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _pipeBlockSize;
    private readonly double _framerate;
    private readonly IMediaAnalysis _mediaInfo;
    private readonly BitmapPool _bitmapPool;
    private bool _disposed = false;

    public double Framerate => _framerate;
    public int Width => _width;
    public int Height => _height;
    public TimeSpan Duration => _mediaInfo.Duration;

    public FFmpegDecodeSession(string videoPath)
    {
        _videoPath = videoPath;
        _mediaInfo = FFProbe.Analyse(_videoPath);

        if (_mediaInfo.VideoStreams.Count == 0)
            throw new InvalidOperationException($"動画ストリームが見つかりません: {_videoPath}");

        _width = _mediaInfo.VideoStreams[0].Width;
        _height = _mediaInfo.VideoStreams[0].Height;
        _pipeBlockSize = ResolvePipeBlockSize(RawFrameBuffer.ResolveFrameSizeOrThrow(_width, _height));
        _framerate = _mediaInfo.VideoStreams[0].FrameRate;
        _bitmapPool = new BitmapPool(_width, _height, 64);
    }

    /// <summary>
    /// 単一フレームを取得する
    /// </summary>
    public async Task<FrameItem?> GetSingleFrameAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        ObjectDisposedException.ThrowIf(_disposed, nameof(FFmpegDecodeSession));

        try
        {
            if (ResolveHardwareDecodeEnabled())
            {
                try
                {
                    var frame = await DecodeSingleFrameInternalAsync(time, useHardwareDecode: true, cancellationToken).ConfigureAwait(false);
                    if (frame is not null)
                    {
                        return frame;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HWデコード失敗。ソフトウェアにフォールバックします: {_videoPath}, {time}, {ex.Message}");
                }
            }

            var fallbackFrame = await DecodeSingleFrameInternalAsync(time, useHardwareDecode: false, cancellationToken).ConfigureAwait(false);
            if (fallbackFrame is null)
            {
                Debug.WriteLine($"フレームの取得に失敗しました: {_videoPath}, {time}");
            }

            return fallbackFrame;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"フレーム取得エラー: {_videoPath}, {time}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 指定された時間範囲のフレームを非同期でデコードする
    /// </summary>
    public async IAsyncEnumerable<FrameItem> DecodeAsync(
        TimeSpan startTime,
        TimeSpan maxLength,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in DecodeInternalAsync(startTime, maxLength, cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <summary>
    /// 1つのFFmpegプロセスで連続デコードする
    /// </summary>
    public async IAsyncEnumerable<FrameItem> DecodeContinuousAsync(
        TimeSpan startTime,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in DecodeInternalAsync(startTime, maxLength: null, cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private async IAsyncEnumerable<FrameItem> DecodeInternalAsync(
        TimeSpan startTime,
        TimeSpan? maxLength,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (startTime < TimeSpan.Zero)
            startTime = TimeSpan.Zero;
        if (maxLength.HasValue && maxLength.Value < TimeSpan.Zero)
            maxLength = TimeSpan.Zero;
        if (maxLength.HasValue && maxLength.Value == TimeSpan.Zero)
            yield break;
        ObjectDisposedException.ThrowIf(_disposed, nameof(FFmpegDecodeSession));

        using var decodeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var decodeToken = decodeCts.Token;

        var channel = CreateDecodeChannel();

        await using var sinkStream = new FrameChunkStream(
            _width,
            _height,
            _bitmapPool,
            _videoPath,
            startTime,
            _framerate,
            channel.Writer,
            decodeToken);

        var ffmpegTask = RunDecodeProcessAsync(startTime, maxLength, sinkStream, channel.Writer, decodeToken);

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(decodeToken))
            {
                yield return frame;
            }
        }
        finally
        {
            decodeCts.Cancel();

            // FFmpegプロセスの完了を待機
            try
            {
                await ffmpegTask;
            }
            catch (OperationCanceledException) when (decodeToken.IsCancellationRequested)
            {
                // キャンセルされた場合は無視
            }

            while (channel.Reader.TryRead(out var remainingFrame))
            {
                remainingFrame.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _bitmapPool.Dispose();
        }
    }

    private async Task RunDecodeProcessAsync(
        TimeSpan startTime,
        TimeSpan? maxLength,
        FrameChunkStream sinkStream,
        ChannelWriter<FrameItem> writer,
        CancellationToken decodeToken)
    {
        try
        {
            string? maxLengthStr = null;
            if (maxLength.HasValue)
            {
                maxLengthStr = maxLength.Value.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);
            }

            if (ResolveHardwareDecodeEnabled())
            {
                try
                {
                    await RunDecodePipelineAsync(
                        startTime,
                        maxLengthStr,
                        sinkStream,
                        decodeToken,
                        useHardwareDecode: true,
                        singleFrame: false).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (decodeToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HWデコード失敗。ソフトウェアにフォールバックします: {_videoPath}, {startTime}, {ex.Message}");
                }
            }

            await RunDecodePipelineAsync(
                startTime,
                maxLengthStr,
                sinkStream,
                decodeToken,
                useHardwareDecode: false,
                singleFrame: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (decodeToken.IsCancellationRequested)
        {
            // linked token 経由の停止は正常系
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FFmpeg処理エラー: {ex.Message}");
            writer.TryComplete(ex);
            return;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private StreamPipeSink CreateRawVideoPipeSink(Stream sink)
    {
        var pipeSink = new StreamPipeSink(sink)
        {
            BlockSize = _pipeBlockSize
        };
        return pipeSink;
    }

    private static System.Threading.Channels.Channel<FrameItem> CreateDecodeChannel()
    {
        return Channel.CreateBounded<FrameItem>(new BoundedChannelOptions(DecodeChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private static int ResolvePipeBlockSize(int frameSize)
    {
        if (frameSize <= 0)
        {
            return MinPipeBlockSize;
        }

        return (int)Math.Clamp(frameSize, MinPipeBlockSize, MaxPipeBlockSize);
    }

    private async Task<FrameItem?> DecodeSingleFrameInternalAsync(TimeSpan time, bool useHardwareDecode, CancellationToken cancellationToken)
    {
        using var sinkStream = new SingleFrameStream(_width, _height, _bitmapPool);
        await RunDecodePipelineAsync(
            time,
            maxLengthStr: null,
            sinkStream,
            cancellationToken,
            useHardwareDecode,
            singleFrame: true).ConfigureAwait(false);

        if (!sinkStream.HasFrame)
        {
            return null;
        }

        return new FrameItem
        {
            Path = _videoPath,
            Time = time,
            Bitmap = sinkStream.TakeBitmap(),
            BitmapReleaser = _bitmapPool.Return
        };
    }

    private async Task RunDecodePipelineAsync(
        TimeSpan startTime,
        string? maxLengthStr,
        Stream sinkStream,
        CancellationToken decodeToken,
        bool useHardwareDecode,
        bool singleFrame)
    {
        await FFMpegArguments
            .FromFileInput(_videoPath, true, opt => ConfigureInputOptions(opt, startTime, useHardwareDecode))
            .OutputToPipe(
                CreateRawVideoPipeSink(sinkStream),
                opt => ConfigureDecodeOutputOptions(opt, maxLengthStr, singleFrame))
            .CancellableThrough(decodeToken)
            .ProcessAsynchronously().ConfigureAwait(false);
    }

    private static FFMpegArgumentOptions ConfigureInputOptions(
        FFMpegArgumentOptions options,
        TimeSpan startTime,
        bool useHardwareDecode)
    {
        options.Seek(startTime);
        if (useHardwareDecode)
        {
            options.WithCustomArgument($"-hwaccel {ResolveHardwareDecodeApi()}");
        }

        return options;
    }

    private FFMpegArgumentOptions ConfigureDecodeOutputOptions(
        FFMpegArgumentOptions options,
        string? maxLengthStr,
        bool singleFrame)
    {
        var configured = ConfigureOutputOptions(options);
        if (singleFrame)
        {
            configured.WithCustomArgument("-frames:v 1");
        }

        if (!string.IsNullOrWhiteSpace(maxLengthStr))
        {
            configured.WithCustomArgument($"-t {maxLengthStr}");
        }

        return configured;
    }

    private FFMpegArgumentOptions ConfigureOutputOptions(FFMpegArgumentOptions options)
    {
        return options
            .ForceFormat("rawvideo")
            .WithSpeedPreset(Speed.UltraFast)
            .WithCustomArgument("-pix_fmt bgra")
            .WithCustomArgument("-an -sn -dn");
    }

    private static bool ResolveHardwareDecodeEnabled()
    {
        var value = Environment.GetEnvironmentVariable(FFmpegPluginEnvironmentVariables.HardwareDecode);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return !value.Equals("0", StringComparison.OrdinalIgnoreCase)
               && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
               && !value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveHardwareDecodeApi()
    {
        var value = Environment.GetEnvironmentVariable(FFmpegPluginEnvironmentVariables.HardwareDecodeApi);
        if (string.IsNullOrWhiteSpace(value))
        {
            return "auto";
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (SupportedHwAccelApis.Contains(normalized))
        {
            return normalized;
        }

        Debug.WriteLine($"未対応の HWデコードAPI '{value}' が指定されました。'auto' を使用します。");
        return "auto";
    }
}
