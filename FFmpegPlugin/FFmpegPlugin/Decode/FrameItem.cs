using SkiaSharp;

namespace FFmpegPlugin.Decode;

public class FrameItem : IDisposable
{
    public string Path { get; set; } = string.Empty;
    public TimeSpan Time { get; set; }
    public required SKBitmap Bitmap { get; set; }
    
    private bool _disposed = false;
    
    public (bool, TimeSpan) IsNearTime(TimeSpan time, TimeSpan threshold)
    {
        var difference = Math.Abs((Time - time).TotalMicroseconds);
        return (difference <= threshold.TotalMicroseconds, TimeSpan.FromMicroseconds(difference));
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            Bitmap?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}