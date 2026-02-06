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

## ベンチ項目

- `RandomSeek_GetSingleFrameAsync`: ランダム位置の単発フレーム取得
- `NearSeek_GetFrameAsync`: 近傍シーク（1〜3フレーム先）を大量反復して計測
- `SequentialDecode_DecodeAsync`: 0.5秒窓の連続デコード
- `Realtime60fps_GetFrameAsync`: 60fps相当の連続要求（60フレーム）を `OperationsPerInvoke=60` で計測
- `SeekLatency_Profiled`: シーク後のフレーム到達時間をサンプリングし、p95/p99などを出力
- `Realtime60fps_JankStats`: 60fps連続要求の遅延分布と閾値超過フレーム数を出力
- `PlaybackAndSeekScenario`: 再生→シークを繰り返すシナリオの遅延分布を出力