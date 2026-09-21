# AirPad — бездротовий екран для iPad на Windows 10/11

> **English summary:** AirPad turns a Windows 10/11 PC into an AirPlay screen-mirroring
> receiver for iPad/iPhone. Open the app, press **Start Receiver**, then pick the PC in
> iPad **Control Center → Screen Mirroring**. Video is hardware-decoded (D3D11VA) with a
> low-latency pipeline; mirror audio plays through the default output device.

`iPad → AirPlay → Windows-приймач → декодоване відео → вікно.` Ніяких власних
протоколів: використовується стандартний **AirPlay Mirroring (legacy-протокол
AirPlay 2)**, який iPadOS підтримує досі (підтверджено проєктом UxPlay 1.73/1.74
для iOS 17/18). Див. деталі в [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

## Як це працює (коротко)

1. Натисни **▶ Start Receiver** — програма піднімає TCP-сервери (5000/7000),
   UDP-аудіо (7002/7003) і реєструє mDNS-сервіси `_airplay._tcp` + `_raop._tcp`.
2. На iPad відкрий **Пункт керування → Повторення екрана** і вибери свій ПК.
3. iPad виконує handshake: `GET /info → /pair-setup → /pair-verify → /fp-setup →
   SETUP → RECORD`, потім ллє H.264-відео (TCP 7000) і AAC-ELD-аудіо (UDP).
4. AirPad розшифровує потік, декодує (D3D11VA, якщо доступне) і показує у вікні
   зі збереженням aspect ratio, включно з автоповоротом portrait/landscape.
5. Завершити можна кнопкою **⏏ Disconnect** / **⏹ Stop** або з iPad.

## Статус проєкту

- ✅ Повний handshake AirPlay mirror (pairing, FairPlay, SETUP/RECORD/FLUSH/TEARDOWN)
- ✅ Прийом і розшифрування H.264 (AES-CTR), AVCC→Annex-B, SPS/PPS
- ✅ Декодування FFmpeg з D3D11VA + software-fallback, рендер у WPF без черг
- ✅ Аудіо AAC-ELD/AAC/ALAC → WASAPI, mute/volume, синхронізація через RTP-таймінги
- ✅ mDNS без Bonjour, визначення IP, попередження про різні підмережі, firewall-скрипт
- ✅ UI: статуси Ready/Waiting/Connected/Streaming, FPS, роздільність, latency, CPU/GPU,
  fullscreen (F11), приховування UI (Ctrl+H), логи, налаштування
- ✅ unit/integration тести (`dotnet test`): живий TCP-сервер на loopback, повний цикл старт/стоп приймача, регресії аудіо/реконекту/mDNS
- ⚠️ E2E з реальним iPad потребує фізичного тестування (див. [`docs/DEBUGGING.md`](docs/DEBUGGING.md))

## Швидкий старт

1. Встанови [.NET 10 SDK](https://dotnet.microsoft.com/download) (для збірки)
   або [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) (для запуску).
2. Збери: `dotnet build AirPad.slnx -c Release`
3. Поклади FFmpeg DLL поруч з `AirPad.exe`:
   `powershell -ExecutionPolicy Bypass -File scripts\get-ffmpeg.ps1`
   (або вручну з [BtbN FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases),
   shared build 8.1: `avcodec/avformat/avutil/swscale/swresample` DLL).
4. Один раз як адміністратор: `scripts\setup-firewall.ps1`.
5. Запусти `src\AirPad\bin\Release\net10.0-windows\AirPad.exe` → **Start Receiver**.
6. На iPad (та сама Wi-Fi мережа): Пункт керування → Повторення екрана → твій ПК.

Без FFmpeg DLL програма запуститься, прийматиме handshake і писатиме в лог
`Video decoder failed to open`; відео з'явиться після встановлення DLL.

## Архітектура

```
src/AirPad.Core/            # уся логіка приймача, без залежності від UI
  Discovery/                # mDNS-анонс, визначення IP, firewall
  Protocol/                 # RTSP/HTTP-сервер, plist-кодек, сесії, обробник команд
  Auth/                     # pairing (X25519+Ed25519) і FairPlay (OmgHax)
  Network/                  # mirror TCP-потік, RAOP UDP-аудіо, dual-mode порт 7000
  Video/                    # H264-шлюз (AVCC→Annex-B), статистика FPS/latency
  Media/                    # FFmpeg-декодери (D3D11VA H.264, AAC/ALAC)
  Audio/                    # WASAPI-плеєр
  Connection/               # ConnectionManager: автомат Ready→Waiting→Connected→Streaming
  Config/ Logging/ Stats/   # налаштування, логи [INFO], CPU/GPU-монітор
src/AirPad/                 # WPF UI (MVVM): головне вікно, налаштування, рендер
tests/AirPad.Core.Tests/    # unit + integration тести
scripts/                    # firewall, завантаження FFmpeg, ffplay-fallback
docs/                       # PROTOCOL / BUILD / DEBUGGING / ARCHITECTURE
```

Пайплайн затримки: `Network → Receive → Decrypt → Decode → GPU Render`
без проміжних черг (коалесцинг кадрів у UI: не більше одного pending-рендера).

## Налаштування

Вікно Settings: назва приймача, порти, автозапуск з Windows, fullscreen при
підключенні, апаратне прискорення, макс. FPS, масштабування (Fit/Fill/Stretch),
аудіо on/off, режим автентифікації, рівень логування. Зберігається в
`%APPDATA%\AirPad\settings.json`. Жодних паролів/ключів не зберігається —
сесійні ключі живуть лише в пам'яті.

## Залежності (всі актуальні на 09.2026)

| Пакет | Версія | Ліцензія | Навіщо |
|---|---|---|---|
| Makaretu.Dns.Multicast | 0.27.0 | MIT | mDNS/DNS-SD без Bonjour |
| BouncyCastle.Cryptography | 2.4.0 | MIT | X25519, Ed25519, AES-CTR/CBC |
| NAudio | 2.2.1 | MIT | WASAPI-аудіовихід |
| FFmpeg.AutoGen | 8.1.0 | LGPL | прив'язки до нативного FFmpeg 8.1 |
| FFmpeg shared build (BtbN) | 8.1 | GPL/LGPL | нативні DLL для декодування |
| xunit | 2.9.3 | Apache-2.0 | тести |

## Документація

- [`docs/PROTOCOL.md`](docs/PROTOCOL.md) — 8 питань протоколу: discovery, порти,
  pairing, відеосесія, формати відео/аудіо, синхронізація, завершення сесії
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — компоненти і потоки даних
- [`docs/BUILD.md`](docs/BUILD.md) — збірка, тести, публікація, залежності
- [`docs/DEBUGGING.md`](docs/DEBUGGING.md) — типові проблеми і як їх діагностувати

## Безпека

- Приймаються лише підключення з локальної мережі (loopback + RFC1918);
  зовнішні IP відхиляються на рівні TCP-accept.
- Стандартний handshake AirPlay (pair-verify + FairPlay); DRM-контент не розшифровується.
- Firewall відкриває тільки потрібні порти (TCP 5000/7000, UDP 5353/7002/7003).

## Подяки та ліцензія

Протокол реалізовано на основі MIT-проєкту
[SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver)
(plist-кодек, моделі RTSP, FairPlay-процедура OmgHax) та документації/референсів
[UxPlay](https://github.com/FDH2/UxPlay),
[openairplay](https://openairplay.github.io/airplay-spec/),
[Emanuele Cozzi AirPlay 2 Internals](https://emanuelecozzi.net/docs/airplay2/).
Вихідний код UxPlay (GPL) **не** копіювався. Ліцензія проєкту: MIT (див. LICENSE).
