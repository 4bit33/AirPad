<#
.SYNOPSIS
    Fallback video viewer: plays the raw H.264 mirror stream via ffplay.
.DESCRIPTION
    Only needed if the built-in FFmpeg decoder cannot load (no DLLs).
    AirPad also writes the Annex-B stream to \\.\pipe\AirPadVideo when
    decoding is unavailable; point any H.264 player at that pipe.

    Usage: .\run-ffplay-pipe.ps1
#>
$ffplay = Get-Command ffplay.exe -ErrorAction SilentlyContinue
if (-not $ffplay) {
    $guess = Join-Path $PSScriptRoot "..\src\AirPad\bin\Release\net10.0-windows\ffplay.exe"
    if (Test-Path -LiteralPath $guess) { $ffplay = @{ Source = $guess } }
}
if (-not $ffplay) {
    Write-Error "ffplay.exe not found. Install FFmpeg (see scripts/get-ffmpeg.ps1) or add it to PATH."
    exit 1
}

& $ffplay.Source -f h264 -probesize 32768 -analyzeduration 0 `
    -fflags nobuffer+discardcorrupt -flags low_delay -framedrop `
    -avioflags direct -vf "setpts=0" "\\.\pipe\AirPadVideo"
