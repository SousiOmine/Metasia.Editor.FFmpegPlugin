using System.Diagnostics;
using FFMpegCore;

public enum BenchmarkVideoProfile
{
    Fhd,
    Uhd4K
}

public sealed record BenchmarkEnvironment(string FhdVideoPath, string Uhd4KVideoPath, string FFmpegBinDirectory)
{
    private const string FhdVideoFileName = "test_fhd_60fps_h264.mp4";
    private const string Uhd4KVideoFileName = "test_4k_60fps_h264.mp4";
    private const string FhdVideoEnvVar = "METASIA_BENCH_VIDEO_FHD";
    private const string Uhd4KVideoEnvVar = "METASIA_BENCH_VIDEO_4K";
    private const string FFmpegDirectoryEnvVar = "METASIA_FFMPEG_BIN_DIR";
    private const int GeneratedUhdDurationSeconds = 12;

    public string ResolveVideoPath(BenchmarkVideoProfile profile)
    {
        return profile switch
        {
            BenchmarkVideoProfile.Fhd => FhdVideoPath,
            BenchmarkVideoProfile.Uhd4K => Uhd4KVideoPath,
            _ => FhdVideoPath
        };
    }

    public static BenchmarkEnvironment Resolve()
    {
        var ffmpegBinDirectory = ResolveFFmpegBinDirectory();
        var fhdVideoPath = ResolveFhdVideoPath();
        var uhd4KVideoPath = ResolveUhd4KVideoPath(ffmpegBinDirectory);

        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = ffmpegBinDirectory
        });

        return new BenchmarkEnvironment(fhdVideoPath, uhd4KVideoPath, ffmpegBinDirectory);
    }

    private static string ResolveFhdVideoPath()
    {
        var envPath = Environment.GetEnvironmentVariable(FhdVideoEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return ValidateVideoPath(envPath, $"{FhdVideoEnvVar} で指定された動画");
        }

        var projectRoot = FindProjectRoot();
        var candidates = new List<string>();
        if (projectRoot is not null)
        {
            candidates.Add(Path.Combine(projectRoot, "Assets", FhdVideoFileName));
        }
        candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Assets", FhdVideoFileName)));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", FhdVideoFileName)));

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        throw new FileNotFoundException(
            $"FHDベンチマーク動画が見つかりません。'{candidates[0]}' に '{FhdVideoFileName}' を配置してください。");
    }

    private static string ResolveUhd4KVideoPath(string ffmpegBinDirectory)
    {
        var envPath = Environment.GetEnvironmentVariable(Uhd4KVideoEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return ValidateVideoPath(envPath, $"{Uhd4KVideoEnvVar} で指定された動画");
        }

        var projectRoot = FindProjectRoot();
        var candidates = new List<string>();
        if (projectRoot is not null)
        {
            candidates.Add(Path.Combine(projectRoot, "Assets", Uhd4KVideoFileName));
        }
        candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Assets", Uhd4KVideoFileName)));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", Uhd4KVideoFileName)));

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        var target = candidates[0];
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        GenerateUhd4KVideo(ffmpegBinDirectory, target);
        return target;
    }

    private static string ResolveFFmpegBinDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable(FFmpegDirectoryEnvVar);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return ValidateFFmpegDirectory(envDir, $"{FFmpegDirectoryEnvVar} で指定されたディレクトリ");
        }

        var projectRoot = FindProjectRoot();
        var candidates = new List<string>();
        if (projectRoot is not null)
        {
            candidates.Add(Path.Combine(projectRoot, "tools", "ffmpeg"));
        }
        candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "ffmpeg")));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg")));

        var found = candidates.FirstOrDefault(HasRequiredFFmpegBinaries);
        if (found is not null)
        {
            return found;
        }

        throw new DirectoryNotFoundException(
            $"ffmpeg バイナリが見つかりません。'{candidates[0]}' に 'ffmpeg.exe' と 'ffprobe.exe' を配置するか、{FFmpegDirectoryEnvVar} を設定してください。");
    }

    private static string ValidateFFmpegDirectory(string directory, string sourceDescription)
    {
        var fullPath = Path.GetFullPath(directory);
        if (HasRequiredFFmpegBinaries(fullPath))
        {
            return fullPath;
        }

        throw new FileNotFoundException(
            $"{sourceDescription} '{fullPath}' に 'ffmpeg.exe' と 'ffprobe.exe' が必要です。");
    }

    private static string ValidateVideoPath(string path, string sourceDescription)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        throw new FileNotFoundException($"{sourceDescription} '{fullPath}' が見つかりません。");
    }

    private static bool HasRequiredFFmpegBinaries(string directory)
    {
        var ffmpegExe = Path.Combine(directory, "ffmpeg.exe");
        var ffprobeExe = Path.Combine(directory, "ffprobe.exe");
        return File.Exists(ffmpegExe) && File.Exists(ffprobeExe);
    }

    private static string? FindProjectRoot()
    {
        var startDirs = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        }.Distinct();

        foreach (var startDir in startDirs)
        {
            var current = new DirectoryInfo(Path.GetFullPath(startDir));
            while (current is not null)
            {
                var csprojPath = Path.Combine(current.FullName, "FFmpegPlugin.Benchmarks.csproj");
                if (File.Exists(csprojPath))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        return null;
    }

    private static void GenerateUhd4KVideo(string ffmpegBinDirectory, string outputPath)
    {
        var ffmpegPath = Path.Combine(ffmpegBinDirectory, "ffmpeg.exe");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"testsrc2=duration={GeneratedUhdDurationSeconds}:size=3840x2160:rate=60");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("28");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpegプロセスの起動に失敗しました。");
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"4Kベンチマーク動画の生成に失敗しました。exit={process.ExitCode}, error={error}");
        }
    }
}
