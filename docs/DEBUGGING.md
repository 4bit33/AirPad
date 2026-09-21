# Debugging AirPad

Логи — перше джерело правди: панель **Log** у вікні + файли
`%APPDATA%\AirPad\logs\`. Рівень — у Settings. Нормальна послідовність:

```
[INFO] Receiver started
[INFO] mDNS service registered
[INFO] Device connected from 192.168.x.x
[INFO] pair-verify step 1 completed
[INFO] Device pairing verified
[INFO] FairPlay key message stored
[INFO] Session SETUP completed (timing + event ports exchanged)
[INFO] Mirror SETUP: stream connection ... / Video stream started
[INFO] Decoder initialized (H.264 Annex-B gate)
[INFO] Streaming 60 FPS
...
[INFO] Device disconnected
```

## iPad не бачить ПК

1. **Одна мережа?** ПК по Ethernet + iPad по Wi-Fi — нормально, аби один роутер/
   підмережа. UI показує попередження, якщо підмереж кілька. Вимкни VPN на ПК.
2. **Firewall.** Запусти `scripts\setup-firewall.ps1` від адміністратора.
   Перевірка: `netsh advfirewall firewall show rule name="AirPad Wireless Display Receiver"`.
3. **mDNS.** UDP 5353 мають пропускати і роутер (IGMP/multicast), і антивірус.
   Гостьові Wi-Fi з client-isolation блокують AirPlay в принципі — перевір на домашній мережі.
4. **Порти зайняті?** `netstat -ano | findstr "7000 5000"` — інший AirPlay-сервер
   (AirServer, Reflector, iTunes) тримає порт → закрий його або зміни порти в Settings.
5. Перезапусти Wi-Fi на iPad (відомий глюк кешу Bonjour) і приймач.

## Підключився, але чорний екран

1. У лозі є `Video decoder failed to open` → немає FFmpeg DLL (див. BUILD.md).
2. Є `Video stream started`, але FPS=0 → дивись `Mirror stream connected`:
   якщо потік класифіковано як control (помилка сніфінгу) — увімкни Debug-логування
   і шукай `Control connection from … on port 7000` одразу після старту відео.
3. `Decode failed` спамить → несумісний H.264-профіль; увімкни Debug, збережи
   значення `Video SPS: WxH` з логу і відкрий issue з цими даними.
4. Старі iPad (32-біт, iOS 9): підтримуються протоколом, але не тестувались —
   напиши нам лог handshake.

## Є відео, немає звуку

1. Settings → audio enabled? Кнопка Mute? Повзунок гучності?
2. У лозі має бути `Audio decoder opened: AAC_ELD` + `Audio output started (WASAPI…)`.
3. `avcodec_open2(ALAC) failed` — стосується лише музичного стрімінгу (ALAC-cookie
   best-effort); mirror-звук (AAC-ELD) на це не впливає.
4. `Audio output failed to start` → зайнятий/вимкнений пристрій виводу Windows;
   перевір мікшер гучності Windows → AirPad.

## Висока затримка / низький FPS

- Очікувано: 30–120 мс у LAN (показує `Latency`). Більше 300 мс — проблема мережі:
  Wi-Fi 2.4 ГГц перевантажений → перейди на 5 ГГц; тримай ПК на Ethernet.
- `FramesDropped` росте при MaxFps-обмеженні — це нормально: такі кадри все одно декодуються
  (P-кадри посилаються один на одного), пропускається лише конвертація і показ.
- Software decode на слабкому CPU: зменши MaxFps до 30.
- D3D11VA не ввімкнулось: онови драйвер GPU; перевір `GpuText` у статус-барі.

## Аварійні завершення / зависання

- `Accept failed on port …` у циклі → порт тримає інший процес (див. netstat вище).
- Зависання при Stop → закрий вікно, процеси `AirPad.exe` вбиваються штатно;
  логи підкажуть, на якому сокеті зупинилось.
- Збери: версія Windows (`winver`), модель iPad + iPadOS, `%APPDATA%\AirPad\logs\`,
  значення SPS/FPS/latency — і відкрий issue.

## YouTube / Netflix / відеододатки зупиняють mirroring

Це нормальна поведінка iOS, а не баг: відеододатки перемикаються з повторення
екрана на пряме відтворення за URL (AirPlay HLS-handoff: `POST /play`, потім
`TEARDOWN` mirror-сесії — видно в лозі). AirPad HLS-плеєр поки не реалізує,
тому екран гасне, а в лозі буде:

```
[INFO] AirPlay video handoff (/play) requested — direct URL playback (HLS) is
not implemented, the iPad stops mirroring here. ...
```

Обхід: дивіться відео через **Safari** — тоді йде звичайне mirroring екрана.
DRM-захищений контент (Apple TV+, Netflix) iPad взагалі не віддзеркалює
стороннім приймачам — покаже чорний прямокутник навіть у Safari.

## Самоперевірка без iPad

```powershell
dotnet test tests\AirPad.Core.Tests\AirPad.Core.Tests.csproj
```

34 тести покривають plist-roundtrip, H264-шлюз, pairing/FairPlay-вектори,
парсинг RTSP, **живий TCP control-сервер на loopback** (OPTIONS + пайплайнінг)
і **повний цикл приймача** (старт mDNS+TCP, RTSP-запит через реальний стек, стоп).
