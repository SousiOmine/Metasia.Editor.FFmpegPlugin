using System.Threading.Channels;

namespace FFmpegPlugin.Decode;

public sealed class FrameChunkStream : Stream
{
    private readonly int _frameSize;
    private readonly ChannelWriter<byte[]> _writer;
    private byte[] _buf;
    private int _filled;

    public FrameChunkStream(int frameSize, ChannelWriter<byte[]> writer)
    {
        _frameSize = frameSize;
        _writer = writer;
        _buf = new byte[_frameSize];
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int copy = Math.Min(count, _frameSize - _filled);
            Buffer.BlockCopy(buffer, offset, _buf, _filled, copy);
            _filled += copy; offset += copy; count -= copy;

            if (_filled == _frameSize)
            {
                // フレーム完成。所有権をチャネルに渡し、新しいバッファを用意
                var completed = _buf;
                _buf = new byte[_frameSize];
                _filled = 0;
                _writer.TryWrite(completed);
            }
        }
    }
    public override void Flush() { }
    protected override void Dispose(bool disposing)
    {
        _writer.TryComplete();
        base.Dispose(disposing);
    }
    // 必須の抽象メンバー（読みは不要）
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
