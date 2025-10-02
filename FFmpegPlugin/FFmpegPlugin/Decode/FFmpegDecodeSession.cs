using System.Runtime.CompilerServices;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using SkiaSharp;
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
    private readonly int _frameSize;
    private readonly string _ffmpegPath;
    private readonly IMediaAnalysis _mediaInfo;
    private bool _disposed = false;

    public FFmpegDecodeSession(string videoPath)
    {
        _videoPath = videoPath;
        _mediaInfo = FFProbe.Analyse(_videoPath);

        if (_mediaInfo.VideoStreams.Count == 0)
            throw new InvalidOperationException($"動画ストリームが見つかりません: {_videoPath}");

        _width = _mediaInfo.VideoStreams[0].Width;
        _height = _mediaInfo.VideoStreams[0].Height;

        _frameSize = _width * _height * 4;
        _ffmpegPath = Path.Combine(GlobalFFOptions.Current.BinaryFolder, "ffmpeg");
    }

    /// <summary>
    /// 単一フレームを取得する
    /// </summary>
    public async Task<FrameItem?> GetSingleFrameAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FFmpegDecodeSession));

        try
        {
            var frameData = await ExtractSingleFrameAsync(time, cancellationToken);
            if (frameData == null)
                return null;

            // BGRAフォーマットのバイト配列からSKBitmapを作成
            var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
            unsafe
            {
                fixed (byte* ptr = frameData)
                {
                    bitmap.SetPixels((IntPtr)ptr);
                }
            }

            return new FrameItem
            {
                Path = _videoPath,
                Time = time,
                Bitmap = bitmap
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"フレーム取得エラー: {_videoPath}, {time}, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 指定された時間範囲のフレームを非同期でデコードする
    /// </summary>
    public async IAsyncEnumerable<FrameItem> DecodeAsync(
        TimeSpan startTime,
        TimeSpan maxLength,
        double frameRate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FFmpegDecodeSession));

        var channel = Channel.CreateUnbounded<byte[]>();

        await using var sinkStream = new FrameChunkStream(_frameSize, channel.Writer);

        // FFmpegプロセスをバックグラウンドで開始
        var ffmpegTask = Task.Run(async () =>
        {
            try
            {
                await FFMpegArguments
                    .FromFileInput(_videoPath)
                    .OutputToPipe(
                        new StreamPipeSink(sinkStream),
                        opt => opt
                            .ForceFormat("rawvideo")
                            .WithHardwareAcceleration()
                            .WithSpeedPreset(Speed.UltraFast)
                            .WithCustomArgument("-pix_fmt bgra")
                            .WithCustomArgument($"-vf fps={frameRate}")
                            .WithCustomArgument("-an -sn -dn")
                            .WithCustomArgument($"-ss {startTime.TotalSeconds}")
                            .WithCustomArgument($"-t {maxLength.TotalSeconds}")
                    )
                    .CancellableThrough(cancellationToken)
                    .ProcessAsynchronously();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg処理エラー: {ex.Message}");
                channel.Writer.Complete(ex);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        // チャネルからフレームデータを読み取り、FrameItemに変換
        int frameIndex = 0;
        await foreach (var frameData in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // BGRAフォーマットのバイト配列からSKBitmapを作成
            var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
            unsafe
            {
                fixed (byte* ptr = frameData)
                {
                    bitmap.SetPixels((IntPtr)ptr);
                }
            }

            // フレームの時間を計算
            TimeSpan frameTime = startTime + TimeSpan.FromSeconds(frameIndex / frameRate);

            yield return new FrameItem
            {
                Path = _videoPath,
                Time = frameTime,
                Bitmap = bitmap
            };

            frameIndex++;
        }

        // FFmpegプロセスの完了を待機
        try
        {
            await ffmpegTask;
        }
        catch (OperationCanceledException)
        {
            // キャンセルされた場合は無視
        }
    }

    /// <summary>
    /// 単一フレームをFFmpegで抽出する
    /// </summary>
    private async Task<byte[]?> ExtractSingleFrameAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();

        try
        {
            await FFMpegArguments
                .FromFileInput(_videoPath)
                .OutputToPipe(
                    new StreamPipeSink(memoryStream),
                    opt => opt
                        .ForceFormat("rawvideo")
                        .WithCustomArgument("-pix_fmt bgra")
                        .WithCustomArgument("-vframes 1")
                        .WithCustomArgument($"-ss {time.TotalSeconds}")
                        .WithCustomArgument("-an -sn -dn")
                )
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously();

            var frameData = memoryStream.ToArray();

            // フレームデータのサイズが正しいかチェック
            if (frameData.Length != _frameSize)
            {
                Console.WriteLine($"フレームサイズが一致しません: 期待={_frameSize}, 実際={frameData.Length}");
                return null;
            }

            return frameData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"単一フレーム抽出エラー: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
