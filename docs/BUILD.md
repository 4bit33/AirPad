# Збірка, запуск, тести, публікація

## Вимоги

- Windows 10 1809+ / 11 x64 (перевірено логікою коду; E2E — з реальним iPad).
- [.NET 10 SDK](https://dotnet.microsoft.com/download) для збірки.
- Для запуску: .NET 10 Desktop Runtime **або** self-contained публікація (див. нижче).
- Git, PowerShell 5.1+.

## Збірка

```powershell
dotnet restore AirPad.slnx
dotnet build AirPad.slnx -c Release
dotnet test tests\AirPad.Core.Tests\AirPad.Core.Tests.csproj
```

Очікувано: `Build succeeded`, `Failed: 0`. Тести, яким потрібні FFmpeg DLL, без них пропускаються;
щоб їх запустити, задай `AIRPAD_FFMPEG_DIR` (папка з `avcodec-*.dll`).

## FFmpeg (обов'язково для відео/аудіо)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-ffmpeg.ps1
```

Скрипт качає shared build 8.1 (BtbN) і кладе `avcodec/avformat/avutil/swscale/swresample`
DLL поруч з `AirPad.exe`. Вручну: [BtbN releases](https://github.com/BtbN/FFmpeg-Builds/releases)
(файл `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`, папка `bin`). Інтернет потрібен **лише тут** —
сама трансляція йде лише локальною мережею.

Перевірка: у лозі після старту має бути `FFmpeg loaded: n8.1 …`,
а після підключення — `H.264 decoder: D3D11VA hardware acceleration enabled`
(або `software decoding` на старих GPU).

## Firewall (один раз, від адміністратора)

```powershell
# PowerShell від адміністратора:
.\scripts\setup-firewall.ps1
```

Або кнопка **Fix Windows Firewall** у Settings (теж попросить elevation через
перезапуск від адміністратора — якщо не спрацювало, запусти скрипт вручну).

## Запуск

```powershell
.\src\AirPad\bin\Release\net10.0-windows\AirPad.exe
```

1. **▶ Start Receiver** (стан `Waiting for device`).
2. iPad у тій самій Wi-Fi мережі: Пункт керування → Повторення екрана → ім'я ПК.
3. Стан `Streaming`, відео + звук. F11 — fullscreen, Ctrl+H — сховати UI,
   Esc — вийти з обох режимів.

## Публікація (один exe для іншого ПК)

```powershell
# Framework-dependent (потрібен .NET 10 Desktop Runtime на цільовому ПК):
dotnet publish src\AirPad\AirPad.csproj -c Release -r win-x64 --self-contained false -o publish\fd

# Self-contained single-file (~70 МБ, рантайм не потрібен):
dotnet publish src\AirPad\AirPad.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\sc
```

Після публікації доклади FFmpeg DLL у папку з exe (скрипт `get-ffmpeg.ps1`
приймає `-Destination publish\sc`).

### Реліз одним кроком

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Проганяє тести, публікує self-contained single-file exe, докладає FFmpeg DLL (+ їхню
LGPL-ліцензію), `README.md`, `LICENSE`, `setup-firewall.ps1` і пакує в
`publish\AirPad-<версія>-win-x64.zip`. Версія береться з `<Version>` у `src/AirPad/AirPad.csproj`.

## Залежності NuGet (фіксовані версії)

`Makaretu.Dns.Multicast 0.27.0`, `BouncyCastle.Cryptography 2.4.0`,
`NAudio 2.2.1`, `FFmpeg.AutoGen 8.1.0`, `xunit 2.9.3`. Відновлення — звичайним
`dotnet restore` з nuget.org.

## Структура налаштувань

`%APPDATA%\AirPad\settings.json` (створюється автоматично), логи —
`%APPDATA%\AirPad\logs\airpad-YYYYMMDD-HHmmss-pPID-nN.log`
(новий файл на кожен запуск; зберігаються останні 30).
