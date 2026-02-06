using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public sealed class FrameCacheItem
{
    public FrameItem Frame { get; }
    public LinkedListNode<long> LruNode { get; }

    public FrameCacheItem(FrameItem frame, LinkedListNode<long> lruNode)
    {
        Frame = frame;
        LruNode = lruNode;
    }
}
