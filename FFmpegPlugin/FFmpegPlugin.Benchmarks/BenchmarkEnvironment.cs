using FFMpegCore;

public sealed record BenchmarkEnvironment(string VideoPath, string FFmpegBinDirectory)
{
    private const string VideoFileName = "test_fhd_60fps_h264.mp4";
    private const string FFmpegDirectoryEnvVar = "METASIA_FFMPEG_BIN_DIR";

    public static BenchmarkEnvironment Resolve()
    {
        var videoPath = ResolveVideoPath();
        var ffmpegBinDirectory = ResolveFFmpegBinDirectory();

        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = ffmpegBinDirectory
        });

        return new BenchmarkEnvironment(videoPath, ffmpegBinDirectory);
    }

    private static string ResolveVideoPath()
    {
        var projectRoot = FindProjectRoot();
        var candidates = new List<string>();
        if (projectRoot is not null)
        {
            candidates.Add(Path.Combine(projectRoot, "Assets", VideoFileName));
        }
        candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Assets", VideoFileName)));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", VideoFileName)));

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        throw new FileNotFoundException(
            $"ベンチマーク動画が見つかりません。'{candidates[0]}' に '{VideoFileName}' を配置してください。");
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
}
