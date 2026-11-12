# Fetches FFmpeg's shared libraries (an LGPL build) into artifacts\ffmpeg-libs, for trying the in-process encoder (issue #225).
# For development only: a Debug build of Yaparr.Desktop copies them next to the program, and the "in-process encoder" launch profile in
# Visual Studio turns the encoder on (YAPARR_INPROCESS=1). Nothing from here is shipped; Yaparr's own build of the libraries is still to come.
#
#   pwsh tools\Get-FfmpegLibs.ps1 [-Url <zip of an FFmpeg shared build>]
param([string]$Url = 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\ffmpeg-libs'
$work = Join-Path ([IO.Path]::GetTempPath()) ('yaparr-ffmpeg-libs-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'ffmpeg.zip'
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x')
    $bin = Get-ChildItem (Join-Path $work 'x') -Directory | ForEach-Object { Join-Path $_.FullName 'bin' } | Where-Object { Test-Path (Join-Path $_ 'avcodec-*.dll') } | Select-Object -First 1
    if (-not $bin) { throw 'There is no avcodec DLL in that download: it is not a shared build.' }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Copy-Item (Join-Path $bin '*.dll') $out -Force
    Write-Host "FFmpeg libraries are in $out"
    Write-Host 'Build Yaparr.Desktop in Debug and pick the "in-process encoder" profile.'
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
