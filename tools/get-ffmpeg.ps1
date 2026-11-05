# Downloads the official LGPL build of FFmpeg 9.0 for Windows (BtbN/FFmpeg-Builds, linked from ffmpeg.org),
# checks it against the published SHA-256 list, and puts ffmpeg.exe + its licence in third_party/ffmpeg.
# The app build embeds that ffmpeg.exe into dBoost.exe.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$name = 'ffmpeg-n9.0-latest-win64-lgpl-9.0'
$base = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'
$dest = Join-Path $PSScriptRoot '..\third_party\ffmpeg'
$work = Join-Path ([IO.Path]::GetTempPath()) ("dboost-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work, $dest | Out-Null

try {
    $zip = Join-Path $work "$name.zip"
    $sums = Join-Path $work 'checksums.sha256'
    Write-Host "Downloading $name.zip ..."
    Invoke-WebRequest "$base/$name.zip" -OutFile $zip -UseBasicParsing
    Invoke-WebRequest "$base/checksums.sha256" -OutFile $sums -UseBasicParsing

    $expected = (Get-Content $sums | Where-Object { $_ -match [regex]::Escape("$name.zip") } | Select-Object -First 1).Split(' ')[0].ToLowerInvariant()
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) { throw "Checksum mismatch: expected $expected, got $actual" }
    Write-Host "SHA-256 OK ($actual)"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -in @("$name/bin/ffmpeg.exe", "$name/LICENSE.txt")) {
                $target = Join-Path $dest $entry.Name
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
                Write-Host "Wrote $target"
            }
        }
    }
    finally { $archive.Dispose() }
    & (Join-Path $dest 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
