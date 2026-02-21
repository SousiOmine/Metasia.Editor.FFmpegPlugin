using System.Diagnostics;
using System.Globalization;
using Metasia.Core.Sounds;

namespace FFmpegPlugin;

internal sealed class AudioSession : IDisposable
{
    private const int TargetChannelCount = 2;
    private const long CacheWindowSamples = 176400;
    private const long CachePrerollSamples = 6615;

    private readonly object _sync = new();
    private readonly string _ffmpegPath;
    private readonly string _mediaPath;
    private bool _disposed;
    private int _cachedSampleRate;
    private AudioChunkCache? _cache;

    public AudioSession(string ffmpegPath, string mediaPath)
    {
        _ffmpegPath = ffmpegPath;
        _mediaPath = mediaPath;
    }

    public async Task<AudioChunk?> GetAudioAsync(TimeSpan? startTime, TimeSpan? duration)
    {
        int defaultSampleRate = 44100;
        long startSample = (long)((startTime ?? TimeSpan.Zero).TotalSeconds * defaultSampleRate);
        long sampleCount = duration.HasValue ? (long)(duration.Value.TotalSeconds * defaultSampleRate) : long.MaxValue;
        
        AudioChunk? chunk = await GetAudioBySampleAsync(startSample, sampleCount, defaultSampleRate).ConfigureAwait(false);
        return chunk;
    }
    
    public async Task<AudioChunk?> GetAudioBySampleAsync(long startSample, long sampleCount, int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AudioSession));

        if (startSample < 0)
        {
            startSample = 0;
        }

        if (sampleCount <= 0)
        {
            return await DecodeAudioAsync(startSample, 0, sampleRate).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (_cache is not null && _cache.SampleRate == sampleRate && TryReadFromCache(startSample, sampleCount, out AudioChunk? cached) && cached is not null)
            {
                return cached;
            }
        }

        long windowStartSample = Math.Max(0, startSample - CachePrerollSamples);
        long windowSampleCount = Math.Max(CacheWindowSamples, sampleCount * 4);
        AudioChunk? windowChunk = await DecodeAudioAsync(windowStartSample, windowSampleCount, sampleRate).ConfigureAwait(false);
        if (windowChunk is null)
        {
            return null;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AudioSession));
            _cachedSampleRate = sampleRate;
            _cache = new AudioChunkCache(windowStartSample, sampleRate, windowChunk);
            return SliceChunk(windowChunk, windowStartSample, startSample, sampleCount);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cache = null;
    }

    private bool TryReadFromCache(long requestStartSample, long requestSampleCount, out AudioChunk? chunk)
    {
        chunk = null;
        if (_cache is null)
        {
            return false;
        }

        long requestEndSample = requestStartSample + requestSampleCount;
        if (requestStartSample < _cache.StartSample || requestEndSample > _cache.EndSample)
        {
            return false;
        }

        chunk = SliceChunk(_cache.Chunk, _cache.StartSample, requestStartSample, requestSampleCount);
        return true;
    }

    private async Task<AudioChunk?> DecodeAudioAsync(long startSample, long sampleCount, int sampleRate)
    {
        if (!File.Exists(_ffmpegPath))
        {
            return null;
        }

        double startSeconds = startSample / (double)sampleRate;
        string startArg = startSeconds.ToString("F6", CultureInfo.InvariantCulture);
        string? durationArg = null;
        if (sampleCount > 0 && sampleCount < long.MaxValue)
        {
            double durationSeconds = sampleCount / (double)sampleRate;
            durationArg = durationSeconds.ToString("F6", CultureInfo.InvariantCulture);
        }

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
        psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
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
        int sampleCountResult = bytes.Length / sizeof(double);
        if (sampleCountResult <= 0)
        {
            return new AudioChunk(new AudioFormat(sampleRate, TargetChannelCount), 0);
        }

        double[] samples = new double[sampleCountResult];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCountResult * sizeof(double));
        return new AudioChunk(new AudioFormat(sampleRate, TargetChannelCount), samples);
    }

    private static AudioChunk SliceChunk(AudioChunk source, long sourceStartSample, double requestStartSample, long requestSampleCount)
    {
        var format = source.Format;
        var output = new AudioChunk(format, requestSampleCount);

        long sourceStartFrame = (long)requestStartSample - sourceStartSample;
        if (sourceStartFrame < 0)
        {
            sourceStartFrame = 0;
        }

        long copyFrames = Math.Min(requestSampleCount, source.Length - sourceStartFrame);
        if (copyFrames <= 0)
        {
            return output;
        }

        int channels = format.ChannelCount;
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

    private sealed class AudioChunkCache(long startSample, int sampleRate, AudioChunk chunk)
    {
        public long StartSample { get; } = startSample;
        public int SampleRate { get; } = sampleRate;
        public AudioChunk Chunk { get; } = chunk;
        public long EndSample => StartSample + Chunk.Length;
    }
}
