# Архітектура AirPad

## Принципи

- Мережа, протокол і декодування живуть у `AirPad.Core` і **нічого не знають про UI**.
  UI (`AirPad`, WPF/MVVM) лише підписується на події і малює кадри.
- Пайплайн затримки без черг: `Network → Receive → Decrypt → Decode → Render`.
  Єдина «черга» — один коалесцований pending-рендер у UI (інакше беклог = лаг).
- Один пристрій mirror одночасно (як Apple TV); аудіо-сесія прив'язується до останньої.

## Компоненти (за ТЗ)

| Вимога ТЗ | Клас(и) | Файл(и) |
|---|---|---|
| discovery | `MdnsAdvertiser`, `NetworkInfo`, `FirewallHelper` | `Discovery/` |
| protocol | `TcpControlServer`, `DualModeServer`, `AirPlayRequestHandler`, plist-кодек, `SessionManager` | `Protocol/`, `Network/` |
| authentication/pairing | `PairingService`, `FairPlayService`, `OmgHax` | `Auth/` |
| network receiver | `MirrorStreamHandler`, `AudioUdpServer` | `Network/` |
| video decoder | `H264Decoder` (FFmpeg D3D11VA→SW), `H264Gate` | `Media/`, `Video/` |
| audio receiver | `AudioDecoder`, `WasapiPlayer` | `Media/`, `Audio/` |
| renderer | WPF `WriteableBitmap` (Bgr24) + `Image` (Uniform) | `AirPad/ViewModels`, `MainWindow` |
| UI | `MainWindow`, `SettingsWindow`, `MainViewModel` | `src/AirPad/` |
| connection manager | `ConnectionManager` (автомат станів) | `Connection/` |
| logging | `AirPadLogger` (`[INFO]`-формат, файл + UI) | `Logging/` |

## Потоки даних

```
iPad ──TCP 5000/7000──▶ TcpControlServer/DualModeServer ──Request──▶ AirPlayRequestHandler
                                   │                                   │ SETUP type 110
                                   │ verdict                           ▼
                                   │                          ConnectionManager
                                   │                          (pendingMirrorSession)
iPad ──TCP 7000 binary──▶ DualModeServer ──claim──▶ MirrorStreamHandler ──H264Packet──▶ ConnectionManager
                                                                                            │ Feed()
                                                                                            ▼
                                                                                   H264Decoder ──VideoFrame──▶ UI (WriteableBitmap)

iPad ──UDP 7002/7003──▶ AudioUdpServer ──decrypt/reorder──▶ AudioDecoder ──PcmPacket──▶ WasapiPlayer
```

## Стани

`Ready → WaitingForDevice → Connected → Streaming`, назад через
TEARDOWN / розрив / Disconnect / Stop. Події: `StateChanged`,
`DeviceNameChanged`, `FrameReady`, `VideoCleared`, `OrientationChanged`,
`FullscreenRequested`.

## Потокова модель

- Accept-loop'и TCP/UDP — фонові таски з `CancellationToken`.
- Mirror- і аудіо-цикли — по одному на з'єднання; декодування — у потоці прийому
  (без хендофів і черг).
- У UI — тільки `Dispatcher.BeginInvoke` для кадрів/логів/статистики.
- Спільний стан сесій — `SessionManager` (ConcurrentDictionary) + дрібні локи.

## Ключові рішення

- **Порти за референсом** (5000 control / 7000 mirror / 7002-7003 audio), але порт
  7000 — dual-mode зі сніфінгом, щоб приймати control і від сучасних клієнтів.
- **BouncyCastle замість Chaos.NaCl/Curve25519-pcl**: ті пакети застарілі й не
  збираються під net10; BouncyCastle дає ті самі X25519/Ed25519.
- **FFmpeg замість ручної збірки fdk-aac/ALAC**: один нативний рантайм для
  H.264 + AAC-ELD + AAC + ALAC; `FFmpegLoader` шукає DLL поруч з exe або в PATH.
- **WriteableBitmap/Bgr24** замість D3DImage: на порядок простіше і надійно;
  нуль-копі D3D11-поверхонь — можлива оптимізація v2 (рендер і так GPU: WPF + D3D11VA-декод).
