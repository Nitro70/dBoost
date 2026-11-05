# Bundled FFmpeg

dBoost does all its audio work with [FFmpeg](https://ffmpeg.org). The release exe carries an unmodified
`ffmpeg.exe` inside it and unpacks it to `%LOCALAPPDATA%\dBoost\ffmpeg\` the first time it starts.

- Build: the official LGPL Windows build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
  (linked from ffmpeg.org's download page), `ffmpeg-n9.0-latest-win64-lgpl-9.0`.
- Version in dBoost 1.0.0: `n9.0.2-47-gb204e94e90-20261105`, built from FFmpeg commit
  [`b204e94e90`](https://github.com/FFmpeg/FFmpeg/commit/b204e94e90) on the `release/9.0` branch.
- Licence: GNU LGPL version 3 or later (`LICENSE.txt` in this folder). The build scripts that
  produced the binary are at https://github.com/BtbN/FFmpeg-Builds.

`ffmpeg.exe` itself is not committed (130 MB). Get it with:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1
```

You can use your own FFmpeg instead: put an `ffmpeg.exe` next to `dBoost.exe`, or set the
`DBOOST_FFMPEG` environment variable to its path. dBoost runs FFmpeg as a separate program and does not
link against it.
