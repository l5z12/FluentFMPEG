# FluentFMPEG

A small FFmpeg GUI for Windows, built with WinUI 3 (Windows App SDK).

Drop a file in, and it figures out what it is and what you probably want to do
with it. Convert formats, extract audio or video, or mux a video file with a
separate audio track — with quality presets, optional advanced flags, and a
bundled FFmpeg that downloads itself on first run.

## Features

- **Drag-and-drop** any media file. `ffprobe` runs automatically and detects
  whether it's video, audio, an image, or an animated image.
- **Auto-suggested format and extension** based on what you dropped. Switching
  the output format swaps the file extension while preserving any custom name
  you typed.
- **Modes**:
  - *Convert* — re-encode to a different container/codec.
  - *Extract audio from video* — `-vn` plus your chosen audio preset.
  - *Extract video (no audio)* — `-an` plus your chosen video preset.
  - *Mix video + audio* — keep the video stream as-is (`-c:v copy`) and remap
    a separate audio file onto it (`-shortest`).
- **Quality presets**: Smaller / Balanced / Higher. Mapped per-codec to CRF
  for x264 / x265 / VP9, bitrate for AAC / MP3 / Opus, and `-q:v` for JPEG /
  WebP.
- **Advanced settings** (collapsible): resolution, frame rate, sample rate,
  channels, audio-bitrate override, trim start/end, and a free-form extra-args
  box that is appended verbatim.
- **Bundled FFmpeg**. On first launch the app fetches a static build from
  [BtbN/FFmpeg-Builds][btbn] (`win64-gpl` or `winarm64-gpl` to match your
  arch) into `%LOCALAPPDATA%\FluentFMPEG\ffmpeg\bin\`. If `ffmpeg`/`ffprobe`
  are already on `PATH`, no download is offered.
- **Automatic GitHub mirror selection**. The download races a small
  `Range: bytes=0-0` probe across the direct `github.com` URL plus several
  prefix-style proxies (`ghfast.top`, `github.moeyy.xyz`, `gh-proxy.com`,
  `ghps.cc`, `mirror.ghproxy.com`); the first 2xx response wins and the full
  download streams through that mirror.
- **Live progress** parsed from `time=` in ffmpeg's stderr, with cancel
  support. The bar is indeterminate while ffmpeg is starting up, then
  switches to determinate once frames are flowing.
- **Mica backdrop**, custom 48-px title bar with the system caption buttons
  set to *Tall* mode.

## Requirements

- Windows 10 1809 or later (Windows 11 recommended for the Mica backdrop).
- .NET 8 SDK with the *Windows desktop* workload, or Visual Studio 2022 with
  the *Windows App SDK* component.
- An internet connection for the first-run FFmpeg download (or a local
  `ffmpeg`/`ffprobe` already on `PATH`).

## Building

```powershell
dotnet build FluentFMPEG/FluentFMPEG.csproj -c Debug -p:Platform=x64
```

Or open `FluentFMPEG.slnx` in Visual Studio and press F5.

Supported platforms: `x86`, `x64`, `ARM64`.

## License

[GPL-3.0](LICENSE).

## Acknowledgments

- FFmpeg static builds: [BtbN/FFmpeg-Builds][btbn].
- Icons: [microsoft/fluentui-system-icons][fluent-icons] (MIT License).

[btbn]: https://github.com/BtbN/FFmpeg-Builds
[fluent-icons]: https://github.com/microsoft/fluentui-system-icons
