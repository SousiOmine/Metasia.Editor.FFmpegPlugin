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
    private readonly string _videoPath;
    private readonly int _width;
    private readonly int _height;
    private readonly double _framerate;
    private readonly IMediaAnalysis _mediaInfo;
    private bool _disposed = false;

    public double Framerate => _framerate;
    public int Width => _width;
    public int Height => _height;

    public FFmpegDecodeSession(string videoPath)
    {
        _videoPath = videoPath;
        _mediaInfo = FFProbe.Analyse(_videoPath);

        if (_mediaInfo.VideoStreams.Count == 0)
            throw new InvalidOperationException($"動画ストリームが見つかりません: {_videoPath}");

        _width = _mediaInfo.VideoStreams[0].Width;
        _height = _mediaInfo.VideoStreams[0].Height;
        _framerate = _mediaInfo.VideoStreams[0].FrameRate;

    }

    /// <summary>
    /// 単一フレームを取得する
    /// </summary>
    public async Task<FrameItem?> GetSingleFrameAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        ObjectDisposedException.ThrowIf(_disposed, nameof(FFmpegDecodeSession));

        using var sinkStream = new SingleFrameStream(_width, _height);

        try
        {
            await FFMpegArguments
                .FromFileInput(_videoPath, true, opt => opt
                    .Seek(time)
                )
                .OutputToPipe(
                    new StreamPipeSink(sinkStream),
                    opt => opt
                        .ForceFormat("rawvideo")
                        .WithSpeedPreset(Speed.UltraFast)
                        .WithCustomArgument("-pix_fmt bgra")
                        .WithCustomArgument("-an -sn -dn")
                        .WithCustomArgument("-frames:v 1")
                )
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously();

            if (!sinkStream.HasFrame)
            {
                Debug.WriteLine($"フレームの取得に失敗しました: {_videoPath}, {time}");
                return null;
            }

            return new FrameItem
            {
                Path = _videoPath,
                Time = time,
                Bitmap = sinkStream.TakeBitmap()
            };
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
        if (startTime < TimeSpan.Zero)
            startTime = TimeSpan.Zero;
        if (maxLength < TimeSpan.Zero)
            maxLength = TimeSpan.Zero;
        ObjectDisposedException.ThrowIf(_disposed, nameof(FFmpegDecodeSession));

        using var decodeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var decodeToken = decodeCts.Token;

        var channel = Channel.CreateBounded<FrameItem>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        await using var sinkStream = new FrameChunkStream(
            _width,
            _height,
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
        }
    }

    private async Task RunDecodeProcessAsync(
        TimeSpan startTime,
        TimeSpan maxLength,
        FrameChunkStream sinkStream,
        ChannelWriter<FrameItem> writer,
        CancellationToken decodeToken)
    {
        try
        {
            var maxLengthStr = maxLength.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);

            await FFMpegArguments
                .FromFileInput(_videoPath, true, opt => opt
                    .Seek(startTime)
                )
                .OutputToPipe(
                    new StreamPipeSink(sinkStream),
                    opt => opt
                        .ForceFormat("rawvideo")
                        .WithSpeedPreset(Speed.UltraFast)
                        .WithCustomArgument("-pix_fmt bgra")
                        .WithCustomArgument("-an -sn -dn")
                        .WithCustomArgument($"-t {maxLengthStr}")
                )
                .CancellableThrough(decodeToken)
                .ProcessAsynchronously();
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
}
