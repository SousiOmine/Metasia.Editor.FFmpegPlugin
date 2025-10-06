using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private readonly double _framerate;
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
        _framerate = _mediaInfo.VideoStreams[0].FrameRate;

        _frameSize = _width * _height * 4;
        _ffmpegPath = Path.Combine(GlobalFFOptions.Current.BinaryFolder, "ffmpeg");
    }

    /// <summary>
    /// 単一フレームを取得する
    /// </summary>
    public async Task<FrameItem?> GetSingleFrameAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FFmpegDecodeSession));

        try
        {
            var frameData = await ExtractSingleFrameAsync(time, cancellationToken);
            if (frameData == null)
                return null;

            // BGRAフォーマットのバイト配列からSKBitmapを作成
            var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
            // GetPixels()で取得したネイティブバッファにコピーして、ポインタのライフタイム問題を回避
            Marshal.Copy(frameData, 0, bitmap.GetPixels(), frameData.Length);

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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FFmpegDecodeSession));

        var channel = Channel.CreateUnbounded<byte[]>();

        await using var sinkStream = new FrameChunkStream(_frameSize, channel.Writer);

        // FFmpegプロセスをバックグラウンドで開始
        var ffmpegTask = Task.Run(async () =>
        {
            try
            {
                // 正確なフレーム取得のため、入力前シークと-accurate_seekを組み合わせ
                //var startTimeStr = startTime.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);
                var maxLengthStr = maxLength.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);
                var frameRateStr = _framerate.ToString("F3", CultureInfo.InvariantCulture);
                
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
                            .WithCustomArgument($"-vf fps={frameRateStr}")
                            .WithCustomArgument("-an -sn -dn")
                            .WithCustomArgument($"-t {maxLengthStr}")
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
            // GetPixels()で取得したネイティブバッファにコピーして、ポインタのライフタイム問題を回避
            Marshal.Copy(frameData, 0, bitmap.GetPixels(), frameData.Length);

            // フレームの時間を計算
            TimeSpan frameTime = startTime + TimeSpan.FromSeconds(frameIndex / _framerate);

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
            // 正確なシークのため、-ssは入力後に配置
            var timeStr = time.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture);
            
            await FFMpegArguments
                .FromFileInput(_videoPath, false, opt => opt
                    .WithCustomArgument("-accurate_seek")  // 正確なシークを有効化
                    .Seek(time)
                )
                .OutputToPipe(
                    new StreamPipeSink(memoryStream),
                    opt => opt
                        .ForceFormat("rawvideo")
                        .WithCustomArgument("-pix_fmt bgra")
                        .WithCustomArgument("-an -sn -dn")
                        .WithCustomArgument("-vsync 0")
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
