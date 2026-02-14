using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public sealed class FrameCacheItem
{
    public FrameItem Frame { get; }
    public long QuantizedTicks { get; }

    public FrameCacheItem(FrameItem frame, long quantizedTicks)
    {
        Frame = frame;
        QuantizedTicks = quantizedTicks;
    }
}
