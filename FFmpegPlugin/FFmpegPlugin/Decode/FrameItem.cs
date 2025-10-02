using SkiaSharp;

namespace FFmpegPlugin.Decode;

public class FrameItem
{
    public string Path { get; set; } = string.Empty;
    public TimeSpan Time { get; set; }
    public required SKBitmap Bitmap { get; set; }
    
    public (bool, TimeSpan) IsNearTime(TimeSpan time, TimeSpan threshold)
    {
        var difference = Math.Abs((Time - time).TotalSeconds);
        return (difference <= threshold.TotalSeconds, TimeSpan.FromSeconds(difference));
    }
}