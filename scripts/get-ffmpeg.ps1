<#
.SYNOPSIS
    Downloads an FFmpeg shared build and places the DLLs next to AirPad.exe.
.DESCRIPTION
    AirPad decodes H.264 video and AAC/ALAC audio with native FFmpeg 8.1
    (libavcodec) via the FFmpeg.AutoGen 8.1 bindings. ABI majors must match,
    so this script fetches the 8.1 branch from BtbN/FFmpeg-Builds.

    Run once after building. No internet is needed afterwards, and none is
    needed for streaming itself (AirPlay stays on the LAN).

    Default (LGPL shared) is enough for decoding; use -License gpl if you
    need GPL components. Use -UrlOverride for a pinned/local archive.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\get-ffmpeg.ps1
.EXAMPLE
    .\scripts\get-ffmpeg.ps1 -Destination .\publish\win-x64 -License gpl
#>
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\src\AirPad\bin\Release\net10.0-windows"),
    [ValidateSet("8.1", "9.0", "master")]
    [string]$Branch = "8.1",
    [ValidateSet("lgpl", "gpl")]
    [string]$License = "lgpl",
    [string]$UrlOverride = ""
)

$ErrorActionPreference = "Stop"

function Get-CandidateUrls {
    param([string]$Branch, [string]$License)
    $urls = @()
    if ($Branch -eq "master") {
        $urls += "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-$License-shared.zip"
    } else {
        # NOTE: BtbN rolling builds repeat the branch version at the end of the
        # file name, e.g. ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip
        $urls += "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n$Branch-latest-win64-$License-shared-$Branch.zip"
    }
    # Fallbacks: other license, then master.
    $other = if ($License -eq "lgpl") { "gpl" } else { "lgpl" }
    if ($Branch -ne "master") {
        $urls += "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n$Branch-latest-win64-$other-shared-$Branch.zip"
    }
    $urls += "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl-shared.zip"
    return $urls
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("airpad-ffmpeg-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp "ffmpeg.zip"
    if ($UrlOverride -ne "") {
        Write-Host "Downloading $UrlOverride ..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $UrlOverride -OutFile $zip
    } else {
        $downloaded = $false
        foreach ($url in Get-CandidateUrls -Branch $Branch -License $License) {
            Write-Host "Trying $url ..." -ForegroundColor Cyan
            try {
                Invoke-WebRequest -Uri $url -OutFile $zip
                $downloaded = $true
                break
            } catch {
                Write-Warning "Not found, trying next candidate."
                if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
            }
        }
        if (-not $downloaded) {
            throw "Could not download any FFmpeg build. Check your connection or download manually from https://github.com/BtbN/FFmpeg-Builds/releases (win64 shared ZIP) and pass it via -UrlOverride <path-or-url>."
        }
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $tmp
    $bin = Get-ChildItem -LiteralPath $tmp -Recurse -Directory -Filter "bin" | Select-Object -First 1
    if (-not $bin) { throw "bin folder not found in archive" }

    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination | Out-Null
    }

    $dlls = @("avcodec-*.dll", "avformat-*.dll", "avutil-*.dll", "swscale-*.dll", "swresample-*.dll")
    $copied = @()
    foreach ($pattern in $dlls) {
        Get-ChildItem -LiteralPath $bin.FullName -Filter $pattern | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Force
            $copied += $_.Name
            Write-Host "  copied $($_.Name)"
        }
    }
    if ($copied.Count -eq 0) { throw "No av*.dll files found in $($bin.FullName)" }

    # Optional helpers: ffplay powers the named-pipe fallback (scripts/run-ffplay-pipe.ps1).
    foreach ($exe in @("ffplay.exe", "ffprobe.exe")) {
        $src = Join-Path $bin.FullName $exe
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination $Destination -Force
            Write-Host "  copied $exe"
        }
    }

    $avcodec = $copied | Where-Object { $_ -like "avcodec-*.dll" } | Select-Object -First 1
    Write-Host "FFmpeg DLLs installed to $Destination ($avcodec)" -ForegroundColor Green
    Write-Host "Start AirPad and look for 'FFmpeg loaded' in the log panel." -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
