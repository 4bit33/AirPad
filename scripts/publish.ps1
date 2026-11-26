<#
.SYNOPSIS
    Builds a release zip: self-contained single-file AirPad.exe + FFmpeg DLLs + docs.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
    # -> publish\AirPad-<version>-win-x64.zip
#>
param([string]$FFmpegZip = "")  # optional local FFmpeg archive, passed to get-ffmpeg.ps1 -UrlOverride

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$version = ([xml](Get-Content -LiteralPath "$root\src\AirPad\AirPad.csproj" -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "AirPad-$version-win-x64"
$out = Join-Path $root "publish\$name"

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

dotnet test "$root\AirPad.slnx" -c Release -nodeReuse:false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

dotnet publish "$root\src\AirPad\AirPad.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -nodeReuse:false -p:UseSharedCompilation=false -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

& "$PSScriptRoot\get-ffmpeg.ps1" -Destination $out -UrlOverride $FFmpegZip
# ffplay/ffprobe only serve the dev-time pipe fallback; keep the release small.
Remove-Item -LiteralPath "$out\ffplay.exe", "$out\ffprobe.exe" -ErrorAction SilentlyContinue

Copy-Item -LiteralPath "$root\LICENSE", "$root\README.md", "$PSScriptRoot\setup-firewall.ps1" -Destination $out

$zip = Join-Path $root "publish\$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Release: $zip" -ForegroundColor Green
