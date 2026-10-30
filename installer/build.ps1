param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$Repository = $env:GITHUB_REPOSITORY
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'samples\Yaparr.Desktop\Yaparr.Desktop.csproj'
$out = Join-Path $root 'artifacts'
$publish = Join-Path $out 'publish'

if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { throw 'No version given and none found in the project file.' }
Write-Host "Building Yaparr $Version ($Runtime)"

Push-Location $root
try {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish $project -c Release -r $Runtime --self-contained true -p:Version=$Version -p:UpdateRepository=$Repository -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    # Bundle the FFmpeg that screen sharing is tested with, next to the app where FfmpegLocator looks first. The pin (URL and hash)
    # lives in FfmpegPackage.Default; the demo tool downloads it through the same code the app uses and refuses a file that differs.
    $download = Join-Path $out 'ffmpeg-download'
    if (Test-Path $download) { Remove-Item $download -Recurse -Force }
    dotnet run --project (Join-Path $root 'tools\Yaparr.ScreenDemo') -c Release -- --install-ffmpeg $download
    if ($LASTEXITCODE) { throw 'could not download the pinned FFmpeg' }
    $ffmpegDir = Get-ChildItem $download -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'ffmpeg.exe') } | Select-Object -First 1
    if (-not $ffmpegDir) { throw 'the pinned FFmpeg was not installed' }
    Copy-Item $ffmpegDir.FullName (Join-Path $publish 'ffmpeg') -Recurse

    dotnet tool restore | Out-Null
    $wixVersion = '5.0.2'
    foreach ($ext in 'WixToolset.UI.wixext', 'WixToolset.Util.wixext') {
        dotnet wix extension add "$ext/$wixVersion" | Out-Null
        if ($LASTEXITCODE) { throw "could not add WiX extension $ext" }
    }
    $msi = Join-Path $out "Yaparr-$Version-$($Runtime -replace '^win-','')."
    $msi = $msi + 'msi'
    dotnet wix build (Join-Path $PSScriptRoot 'Package.wxs') -arch x64 -d Version=$Version -d PublishDir=$publish -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -o $msi
    if ($LASTEXITCODE) { throw 'wix build failed' }

    Write-Host ("Created {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
}
finally { Pop-Location }
