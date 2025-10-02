using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public class FrameCacheItem
{
    public FrameItem Frame { get; }
    public DateTime LastUsedTime { get; set; }
    
    public FrameCacheItem(FrameItem frame, DateTime lastUsedTime)
    {
        Frame = frame;
        LastUsedTime = lastUsedTime;
    }
}