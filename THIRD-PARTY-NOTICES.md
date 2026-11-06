# Third-party notices

dBoost's own code is MIT licensed (see `LICENSE`). The release exe also contains the following.

## FFmpeg

- What: an unmodified `ffmpeg.exe`, embedded in `dBoost.exe` and unpacked to
  `%LOCALAPPDATA%\dBoost\ffmpeg\` on first start. dBoost runs it as a separate program.
- Version: `n9.0.2-47-gb204e94e90-20261105`, the LGPL build from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (`ffmpeg-n9.0-latest-win64-lgpl-9.0`).
- Source: https://github.com/FFmpeg/FFmpeg/commit/b204e94e90 (branch `release/9.0`); build scripts at
  https://github.com/BtbN/FFmpeg-Builds.
- Licence: GNU Lesser General Public License version 3 or later. The full text is in
  `third_party/ffmpeg/LICENSE.txt`, is included in the release zip, and is written next to the unpacked
  `ffmpeg.exe`.
- You can replace it: put your own `ffmpeg.exe` next to `dBoost.exe` or point the `DBOOST_FFMPEG`
  environment variable at one.

That FFmpeg build includes these libraries, each under its own licence (LGPL-compatible):
LAME (libmp3lame, LGPL), Opus (BSD), Vorbis and Ogg (BSD), and others listed by `ffmpeg -version`.
See https://ffmpeg.org/legal.html.

## NAudio

The player uses [NAudio](https://github.com/naudio/NAudio) 2.2.1 (NAudio.Core and NAudio.WinMM) to send
sound to Windows. MIT licence, Copyright 2020 Mark Heath.

## .NET

The exe is a self-contained .NET 10 application and includes the .NET runtime and WPF
(MIT licensed, https://github.com/dotnet/runtime and https://github.com/dotnet/wpf).
