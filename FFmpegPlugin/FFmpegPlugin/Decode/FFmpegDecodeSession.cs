using System.Threading.Channels;
using FFMpegCore;
using FFMpegCore.Pipes;
using SkiaSharp;

namespace FFmpegPlugin.Decode;

/// <summary>
/// 1つの動画ファイルごとにでコード処理を担当する 
/// </summary>
public class FFmpegDecodeSession : IDisposable
{
    private readonly string _videoPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameSize;
    private readonly string _ffmpegPath;
    
    public FFmpegDecodeSession(string videoPath)
    {
        _videoPath = videoPath;
        IMediaAnalysis mediaInfo = FFProbe.Analyse(_videoPath);
        
        _width = mediaInfo.VideoStreams[0].Width;
        _height = mediaInfo.VideoStreams[0].Height;
        
        _frameSize = _width * _height * 4;
        _ffmpegPath = Path.Combine(GlobalFFOptions.Current.BinaryFolder, "ffmpeg");
    }

    public async IAsyncEnumerable<FrameItem> DecodeAsync(TimeSpan startTime, TimeSpan maxLength, double frameRate)
    {
        var channel = Channel.CreateUnbounded<byte[]>();

        await using var sinkStream = new FrameChunkStream(_frameSize, channel.Writer);
        using var cancellationTokenSource = new CancellationTokenSource();
        
        // FFmpegプロセスをバックグラウンドで開始
        var ffmpegTask = Task.Run(async () =>
        {
            await FFMpegArguments
                .FromFileInput(_videoPath)
                .OutputToPipe(
                    new StreamPipeSink(sinkStream),
                    opt => opt
                        .ForceFormat("rawvideo")
                        .WithCustomArgument("-pix_fmt bgra")
                        .WithCustomArgument($"-vf fps={frameRate}")
                        .WithCustomArgument("-an -sn -dn")
                        .WithCustomArgument($"-ss {startTime.TotalSeconds}")
                        .WithCustomArgument($"-t {maxLength.TotalSeconds}")
                )
                .CancellableThrough(cancellationTokenSource.Token)
                .ProcessAsynchronously();
        });

        
        // チャネルからフレームデータを読み取り、FrameItemに変換
        int frameIndex = 0;
        await foreach (var frameData in channel.Reader.ReadAllAsync())
        {
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
        await ffmpegTask;
    }

    public void Dispose()
    {
    }
}
