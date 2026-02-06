# FFmpegPlugin.Benchmarks

`FFmpegPlugin` のベンチマーク

## 準備

1. `tools/ffmpeg/` に `ffmpeg.exe` と `ffprobe.exe` を配置 
   もしくは環境変数 `METASIA_FFMPEG_BIN_DIR` で配置先を指定
2. ベンチマーク動画は `Assets/test_fhd_60fps_h264.mp4`

## 実行

```powershell
dotnet run -c Release --project FFmpegPlugin.Benchmarks
```

## 結果

`BenchmarkDotNet.Artifacts/results` に Markdown / HTML / CSV が配置される
