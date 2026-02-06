using SkiaSharp;

namespace FFmpegPlugin.Decode;

public class FrameItem : IDisposable
{
    public string Path { get; set; } = string.Empty;
    public TimeSpan Time { get; set; }
    public required SKBitmap Bitmap { get; set; }
    private SKImage? _image;
    
    private bool _disposed = false;
    
    public (bool, TimeSpan) IsNearTime(TimeSpan time, TimeSpan threshold)
    {
        var difference = Math.Abs((Time - time).TotalMicroseconds);
        return (difference <= threshold.TotalMicroseconds, TimeSpan.FromMicroseconds(difference));
    }

    public SKImage GetImage()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FrameItem));
        _image ??= SKImage.FromBitmap(Bitmap);
        return _image;
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _image?.Dispose();
            Bitmap?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
