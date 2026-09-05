#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates Windows Firewall inbound rules for AirPad (AirPlay receiver).
.DESCRIPTION
    Run once as Administrator. Opens:
      TCP 7000 (AirPlay), 5000 (AirTunes/RTSP control)
      UDP 5353 (mDNS discovery), 7002/7003 (RAOP audio)
    Uses the ports from %APPDATA%\AirPad\settings.json when present.
#>
param(
    [int]$AirPlayPort = 7000,
    [int]$AirTunesPort = 5000,
    [int]$AudioControlPort = 7002,
    [int]$AudioDataPort = 7003
)

$settingsPath = Join-Path $env:APPDATA "AirPad\settings.json"
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $s = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($s.AirPlayPort) { $AirPlayPort = [int]$s.AirPlayPort }
        if ($s.AirTunesPort) { $AirTunesPort = [int]$s.AirTunesPort }
        if ($s.AudioControlPort) { $AudioControlPort = [int]$s.AudioControlPort }
        if ($s.AudioDataPort) { $AudioDataPort = [int]$s.AudioDataPort }
    } catch { Write-Warning "Could not read settings.json, using defaults." }
}

$ruleName = "AirPad Wireless Display Receiver"
Write-Host "Configuring Windows Firewall for AirPad..." -ForegroundColor Cyan

netsh advfirewall firewall delete rule name="$ruleName" | Out-Null

netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport="$AirPlayPort,$AirTunesPort" profile=private,public | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to add TCP rule (exit $LASTEXITCODE)" }

netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=UDP localport="5353,$AudioControlPort,$AudioDataPort" profile=private,public | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to add UDP rule (exit $LASTEXITCODE)" }

Write-Host "Done. Inbound rules installed:" -ForegroundColor Green
Write-Host "  TCP $AirPlayPort,$AirTunesPort   UDP 5353,$AudioControlPort,$AudioDataPort"
