using System.Runtime.CompilerServices;

namespace FFmpegPlugin.Decode;

internal static class RawFrameBuffer
{
    internal const int BytesPerPixel = 4;

    internal static int ResolveFrameSizeOrThrow(int width, int height, string? paramName = null)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be greater than 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than 0.");
        }

        var frameSize = (long)width * height * BytesPerPixel;
        if (frameSize > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName ?? nameof(width), "Frame size is too large.");
        }

        return (int)frameSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void CopyToUnmanaged(byte[] source, int sourceOffset, IntPtr destination, int count)
    {
        var sourceSpan = source.AsSpan(sourceOffset, count);
        var destinationSpan = new Span<byte>((void*)destination, count);
        sourceSpan.CopyTo(destinationSpan);
    }
}
