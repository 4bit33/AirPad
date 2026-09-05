# AirPlay/AirPlay Mirroring — що саме реалізовано і чому так

Цей документ відповідає на 8 обов'язкових питань ТЗ. Висновки звірено з
актуальними (2025–2026) джерелами: UxPlay 1.73/1.74, openairplay-spec,
Emanuele Cozzi «AirPlay 2 Internals», air-display «AirPlay Internal And
Implementation», SteeBono/airplayreceiver (MIT), moieric11/AirPlay-Windows.

Головний висновок: **сучасний iPadOS (17/18) досі підтримує legacy-протокол
AirPlay Mirroring** (той самий, що й AirPlay 1 mirror + розширення AirPlay 2
для pairing). Саме його реалізує UxPlay, і саме його реалізовано в AirPad.
Ніякого власного протоколу не вигадано.

## 1. Discovery-протоколи

- **mDNS/DNS-SD (RFC 6762/6763)**, multicast UDP 224.0.0.251:5353.
- Два сервіси:
  - `_airplay._tcp.local` — керування/можливості (TXT: `deviceid`, `features`,
    `flags`, `model=AppleTV5,3`, `pk`, `pi`, `srcvers=220.68`, `vv=2`);
  - `_raop._tcp.local` (RAOP = Remote Audio Output Protocol) — аудіо+mirror
    control (TXT: `ch=2`, `cn=2,3`, `et=0,3,5`, `md=0,1,2`, `sr=44100`,
    `ss=16`, `ft=0x5A7FFFF7,0x1E`, `am=AppleTV5,3`, `sf=0x4`, `tp=UDP` …).
- `features=0x5A7FFFF7,0x1E` заявляє Video/Photo/Mirroring/Audio/Volume/HLS —
  саме цей набір показує ПК у «Повторенні екрана».
- AirPad використовує чистий C# mDNS (Makaretu.Dns, MIT) — **Bonjour/Avahi не потрібні**.
  Реалізація: `src/AirPad.Core/Discovery/MdnsAdvertiser.cs`.

## 2. Порти

| Порт | Протокол | Призначення |
|---|---|---|
| 7000 TCP | AirPlay (HTTP+RTSP) + mirror | control (fallback) і сирий H.264-потік |
| 5000 TCP | RAOP control (HTTP+RTSP) | основний control-plane: /info, pairing, SETUP |
| 5353 UDP | mDNS | discovery |
| 7002 UDP | RAOP control | timing-sync (0x54), ретрансміти (0x55/0x56) |
| 7003 UDP | RAOP data | RTP-аудіо |

Контрольні з'єднання persistent (keep-alive) і мультиплексують HTTP та RTSP
на одному сокеті («XTXP»). Порт 7000 у AirPad — dual-mode: за першими байтами
визначає control-запит чи сирий mirror-потік (`Network/DualModeServer.cs`).
Усі порти, крім mDNS, налаштовуються в Settings; firewall-скрипт відкриває саме їх.

## 3. Pairing / authentication

1. `POST /pair-setup` → сервер віддає 32-байтний Ed25519-публічний ключ.
2. `POST /pair-verify` крок 1 (флаг 1): клієнт шле свій ECDH X25519-публічний
   ключ (32 б) + Ed25519-ключ (32 б). Сервер генерує ефемерну X25519-пару,
   рахує shared secret, підписує `ECDH_ours‖ECDH_theirs` своїм Ed25519-ключем,
   шифрує підпис AES-CTR (ключ/IV = SHA-512(`"Pair-Verify-AES-Key/IV"` + shared))
   і повертає `ECDH_ours (32) ‖ enc(sig) (64)`.
3. `POST /pair-verify` крок 2 (флаг 0): клієнт шле свій підпис; сервер
   розшифровує і перевіряє Ed25519. `PairVerified=true` відкриває /fp-setup.
4. `POST /fp-setup` фаза 1 (16 байт, mode 0–3) → 142-байтна відповідь;
   фаза 2 (164 байти) → сервер зберігає key message і повертає 32 байти
   (`FPLY`-заголовок + хвіст). Сесійний AES-ключ (`ekey`) розшифровується
   процедурою OmgHax (відкритий реверс-інжиніринг, MIT).
5. Криптографія — BouncyCastle (X25519/ECDH, Ed25519, AES-CTR/CBC). Жодних
   ключів Apple не вбудовано; DRM-відео не розшифровується.

Реалізація: `Auth/PairingService.cs`, `Auth/FairPlayService.cs`.

## 4. Встановлення відеосесії

1. `SETUP rtsp://…/<id>` з binary-plist: `ekey`, `eiv`, `et`,
   `isScreenMirroringSession=1`, `timingPort`, `sessionUUID` →
   відповідь `{timingPort, eventPort}`.
2. `SETUP` зі `streams=[{type:110, streamConnectionID}]` →
   відповідь `streams=[{type:110, dataPort:7000}]`.
3. `GET_PARAMETER` (volume), `RECORD` (старт), періодичні `POST /feedback`
   (keep-alive, відповідаємо 200 OK), `SET_PARAMETER` (volume/progress/cover-art).
4. Клієнт відкриває **нове TCP-з'єднання на 7000** і ллє кадри:
   128-байтний заголовок (`MirroringHeader`: розмір, тип, NTP/PTS, роздільність)
   + AES-CTR-шифрований payload. Тип 1 = avcC (SPS/PPS), тип 0 = відео.
   Відеоключ: `SHA-512(ekey‖shared)` → HKDF-подібні хеші
   `"AirPlayStreamKey/IV"+streamConnectionID` (див. `MirrorStreamHandler.CreateStreamCipher`).

## 5. Формат відео

- **H.264**, length-prefixed NALU (AVCC, 4-байтний big-endian розмір) →
  конвертуємо в Annex-B (start-code `00 00 00 01`).
- SPS/PPS приходять окремо (avcC-бокс) і підставляються перед IDR (NAL type 5).
- Роздільність/орієнтація — з заголовка типу 1 (`WidthSource/HeightSource`)
  + парсинг SPS; зміна portrait↔landscape детектується на льоту.
- Декодування: FFmpeg (D3D11VA → BGR24 → WPF), software-fallback.

## 6. Формат аудіо

- Mirror: **AAC-ELD**, 44100 Гц, стерео, `constantDuration=480`
  (`audioFormat=0x1000000`), RTP/UDP, AES-CBC.
- Music-streaming: **ALAC** (`0x40000`, 352 фрейми) або AAC (`0x400000`).
- Ключ аудіо: `SHA-512(DecryptedAesKey‖shared)[0..16]`, IV = `eiv`,
  AES-CBC/NoPadding поблоково; хвіст <16 байт копіюється як є.
- Джіттер-буфер 256 пакетів за seq, запити ретрансмітів (0x55), декод FFmpeg
  (ASC-пресети) → resample в s16/stereo/44100 → WASAPI. Гучність/mute — через
  `SET_PARAMETER volume:`.

## 7. Timing / синхронізація

- NTP-подібний sync: control-пакети 0x54 несуть NTP-час відправника і наступний
  RTP-timestamp; PTS відео — NTP-мітки з 128-байтних заголовків.
- A/V-sync v1: аудіо йде з мінімальним буфером (400 мс cap, drop-on-overflow),
  відео рендериться одразу після декоду; UI показує оцінкову latency
  (network-received → rendered, EMA). Цього достатньо для екрану iPad
  (допустимий A/V-дрейф <100 мс на практиці через спільний clock відправника).

## 8. Завершення сесії

- `TEARDOWN` зі `streams=[{type:110}]` → зупинка відео, flush декодера;
  `type:96` → flush аудіо; без тіла → повний teardown сесії.
- `FLUSH` з `RTP-Info: seq=N` → скидання аудіо-вікна до N.
- Розрив TCP → ті самі колбеки (`Device disconnected` у лозі).
- Кнопки UI: **Disconnect** (скинути сесію, продовжити advertise) і
  **Stop** (зупинити все + зняти mDNS).
