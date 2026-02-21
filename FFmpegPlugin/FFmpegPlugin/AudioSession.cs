using System.Diagnostics;
using System.Globalization;
using Metasia.Core.Sounds;

namespace FFmpegPlugin;

internal sealed class AudioSession : IDisposable
{
    private const int TargetSampleRate = 44100;
    private const int TargetChannelCount = 2;
    private const double CacheWindowSeconds = 4.0;
    private const double CachePrerollSeconds = 0.15;

    private readonly object _sync = new();
    private readonly string _ffmpegPath;
    private readonly string _mediaPath;
    private bool _disposed;
    private AudioChunkCache? _cache;

    public AudioSession(string ffmpegPath, string mediaPath)
    {
        _ffmpegPath = ffmpegPath;
        _mediaPath = mediaPath;
    }

    public async Task<AudioChunk?> GetAudioAsync(TimeSpan? startTime, TimeSpan? duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AudioSession));

        double startSeconds = Math.Max(0, (startTime ?? TimeSpan.Zero).TotalSeconds);
        double durationSeconds = duration.HasValue && duration.Value > TimeSpan.Zero
            ? duration.Value.TotalSeconds
            : 0;

        if (durationSeconds <= 0)
        {
            return await DecodeAudioAsync(startSeconds, null).ConfigureAwait(false);
        }

        long requiredFrames = SecondsToFramesCeil(durationSeconds);
        if (requiredFrames <= 0)
        {
            return new AudioChunk(new AudioFormat(TargetSampleRate, TargetChannelCount), 0);
        }

        lock (_sync)
        {
            if (TryReadFromCache(startSeconds, requiredFrames, out AudioChunk? cached) && cached is not null)
            {
                return cached;
            }
        }

        double windowStartSeconds = Math.Max(0, startSeconds - CachePrerollSeconds);
        double windowDurationSeconds = Math.Max(CacheWindowSeconds, durationSeconds * 4.0);
        AudioChunk? windowChunk = await DecodeAudioAsync(windowStartSeconds, windowDurationSeconds).ConfigureAwait(false);
        if (windowChunk is null)
        {
            return null;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AudioSession));
            _cache = new AudioChunkCache(windowStartSeconds, windowChunk);
            return SliceChunk(windowChunk, windowStartSeconds, startSeconds, requiredFrames);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cache = null;
    }

    private bool TryReadFromCache(double requestStartSeconds, long requestFrames, out AudioChunk? chunk)
    {
        chunk = null;
        if (_cache is null)
        {
            return false;
        }

        long requestStartFrame = SecondsToFramesFloor(requestStartSeconds);
        long requestEndFrame = requestStartFrame + requestFrames;
        if (requestStartFrame < _cache.StartFrame || requestEndFrame > _cache.EndFrame)
        {
            return false;
        }

        chunk = SliceChunk(_cache.Chunk, _cache.WindowStartSeconds, requestStartSeconds, requestFrames);
        return true;
    }

    private async Task<AudioChunk?> DecodeAudioAsync(double startSeconds, double? durationSeconds)
    {
        if (!File.Exists(_ffmpegPath))
        {
            return null;
        }

        string startArg = startSeconds.ToString("F6", CultureInfo.InvariantCulture);
        string? durationArg = durationSeconds.HasValue
            ? durationSeconds.Value.ToString("F6", CultureInfo.InvariantCulture)
            : null;

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startArg);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(_mediaPath);
        if (durationArg is not null)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(durationArg);
        }
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("f64le");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_f64le");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var output = new MemoryStream();
        Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            Debug.WriteLine($"GetAudioAsync ffmpeg error: {stderrTask.Result}");
            return null;
        }

        byte[] bytes = output.ToArray();
        int sampleCount = bytes.Length / sizeof(double);
        if (sampleCount <= 0)
        {
            return new AudioChunk(new AudioFormat(TargetSampleRate, TargetChannelCount), 0);
        }

        double[] samples = new double[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * sizeof(double));
        return new AudioChunk(new AudioFormat(TargetSampleRate, TargetChannelCount), samples);
    }

    private static AudioChunk SliceChunk(AudioChunk source, double sourceStartSeconds, double requestStartSeconds, long requestFrames)
    {
        var format = new AudioFormat(TargetSampleRate, TargetChannelCount);
        var output = new AudioChunk(format, requestFrames);

        long sourceStartFrame = SecondsToFramesFloor(requestStartSeconds - sourceStartSeconds);
        if (sourceStartFrame < 0)
        {
            sourceStartFrame = 0;
        }

        long copyFrames = Math.Min(requestFrames, source.Length - sourceStartFrame);
        if (copyFrames <= 0)
        {
            return output;
        }

        int channels = TargetChannelCount;
        for (long frame = 0; frame < copyFrames; frame++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                long srcIndex = ((sourceStartFrame + frame) * channels) + ch;
                long dstIndex = (frame * channels) + ch;
                output.Samples[dstIndex] = source.Samples[srcIndex];
            }
        }

        return output;
    }

    private static long SecondsToFramesFloor(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }

        return (long)Math.Floor(seconds * TargetSampleRate);
    }

    private static long SecondsToFramesCeil(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }

        return Math.Max(1, (long)Math.Ceiling(seconds * TargetSampleRate));
    }

    private sealed class AudioChunkCache(double windowStartSeconds, AudioChunk chunk)
    {
        public double WindowStartSeconds { get; } = windowStartSeconds;
        public AudioChunk Chunk { get; } = chunk;
        public long StartFrame => SecondsToFramesFloor(WindowStartSeconds);
        public long EndFrame => StartFrame + Chunk.Length;
    }
}
