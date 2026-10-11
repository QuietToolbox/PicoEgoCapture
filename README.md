# PicoEgoCapture

**English** | [简体中文](README.zh-CN.md)

<a href="https://quiettoolbox.github.io/"><img src="https://quiettoolbox.github.io/assets/quiettoolbox-avatar.jpg" width="32" height="32" alt="QuietToolbox"></a> **[← Toolbox Home](https://quiettoolbox.github.io/)**

Maintained by [QuietToolbox](https://github.com/QuietToolbox).

First-person (egocentric) data collection app for the **PICO 4 Ultra** headset: records a synchronized **first-person video + head pose + both hands' 26-joint 3D poses**, in a format compatible with EgoKit, for training robot manipulation / VLA / world models.

## Two apps (same code, two build profiles)

This project ships as two apps that can be installed **side by side** on the headset (different package names):

| App | Package | Use | Preview |
|---|---|---|---|
| **PicoEgoCapture** | `com.DefaultCompany.EgoRepro` | **Data collection** — wear it and record demonstrations | HUD preview that keeps the real view usable |
| **PicoEgoView** | `com.DefaultCompany.PicoEgoView` | **Demo / mirroring** — cast the first-person view + hand skeleton to a PC/screen (e.g. presentations) | Large, fills the view |

Both build from the same code; they differ only in name + package and preview size (PicoEgoCapture uses a small HUD panel ~1 m in front; PicoEgoView fills the view). The **`main`** branch is PicoEgoCapture; the **`picoegoview`** branch is PicoEgoView. Because the package names differ, installing one does not overwrite the other.

**To (re)build a specific variant:**
1. Switch branch: `git checkout main` for PicoEgoCapture, or `git checkout picoegoview` for PicoEgoView.
2. Close and reopen the Unity project so it reloads that branch's product name and package (Unity does not always pick these up live).
3. `File` then `Build And Run`.

If you build without switching, you get the current branch (default `main` = PicoEgoCapture). The two apps coexist on the headset, so building one never removes the other.

## Quick Start

### Source distribution

This public repository distributes source code. No verified public APK is currently provided. Build the app with Unity and the separately installed PICO SDK using the instructions below.

### Build from source

Step-by-step, assuming you have never used Unity:

1. **Install Unity Hub** from <https://unity.com/download>.
2. In Unity Hub → **Installs → Install Editor → Unity 2022.3.x (LTS)**. On the **"Add modules"** screen you **must** tick **Android Build Support**, and expand it to also tick **OpenJDK** and **Android SDK & NDK Tools** (without these you cannot build for the headset).
3. **Get the PICO SDK:** register at <https://developer.picoxr.com/>, download the **PICO Unity Integration SDK**, and unzip it somewhere, e.g. `C:/pico-sdk/PICO-Unity-Integration-SDK`.
4. **Clone this repo.** Open `Packages/manifest.json` in any text editor and change the line
   `"com.unity.xr.picoxr": "file:../../PICO-Unity-Integration-SDK",`
   to point to **your** SDK folder using forward slashes, e.g. `"file:C:/pico-sdk/PICO-Unity-Integration-SDK"`.
5. In Unity Hub → **Projects → Add**, select the cloned folder, and open it with **2022.3.x**. The first import takes several minutes.
6. If a **PICO project-config popup** appears, click **"Apply All"** (it sets Android + ARM64 + IL2CPP and enables the PICO XR plugin).
7. Connect the Pico 4 Ultra (Developer Mode + USB Debugging on; accept the prompt in the headset).
8. **File → Build Settings**: set the platform to **Android** (click *Switch Platform* if needed), then click **Build And Run**.
9. On first launch in the headset, **allow Camera**, then grant **All files access** (`adb shell appops set com.DefaultCompany.EgoRepro MANAGE_EXTERNAL_STORAGE allow`, or Settings → Apps → PicoEgoCapture → Permissions).

**Troubleshooting**
- *Build does nothing / shows old errors:* this Unity edition sometimes won't recompile external edits — **close and reopen the project** to force a clean rebuild, and verify the output APK's timestamp updated.
- *"JDK not found":* open **Edit → Preferences → External Tools** once so Unity re-detects the bundled JDK / SDK / NDK.
- *Gradle download hangs (in mainland China):* set a proxy + domestic mirrors in `~/.gradle/`, or use a VPN, before building.

## 1. What it produces

Each recording session creates `/sdcard/DCIM/Recordings/<yyyyMMdd_HHmmss>/` with **four files**:

| File | Content |
|---|---|
| `internal.mp4` | First-person video, H.264, **1280×960 @ 60fps** (upright, raw camera, no overlay) |
| `poses.txt` | Per frame: head pose + both hands' 26 joints (3D position + rotation), **373 columns**, OpenXR joint order |
| `log.txt` | Session metadata: start/end time, per-file frame count, first/last frame unix-ms timestamp, duration (EgoKit-compatible) |
| `frame_timestamps.txt` | Per video-frame unix-ms capture time, for precise video↔pose alignment (our addition; EgoKit only stores first/last) |

**`poses.txt` line format** (space-separated, 373 fields):
```
idx  t_ms  head_px head_py head_pz  head_qx head_qy head_qz head_qw \
     L0_px L0_py L0_pz L0_qx L0_qy L0_qz L0_qw ... L25_(7) \
     R0_(7) ... R25_(7)
```
- `idx` frame index, `t_ms` ms relative to first sample.
- Head: 3 position + 4 quaternion.
- Left hand: 26 joints × (3 pos + 4 quat) = 182. Right hand: same 182.
- Untracked joint → `0 0 0 0 0 0 1`.
- Joint order (OpenXR `XR_HAND_JOINT_*`): `0=Palm 1=Wrist 2..5=Thumb 6..10=Index 11..15=Middle 16..20=Ring 21..25=Little`.

## 2. Architecture

Everything is built at runtime by `EgoBootstrap` (no manual scene setup needed). Data flow:

```
PICO RGB camera ──(PXR_CameraImage, RGBA CPU buffer)──> EgoCameraSource.Update()
                                                              |
                          +------------- OnFrame event -------+---------------+
                          v                                                   v
                  preview Texture2D                                  EgoRecorder.HandleFrame
                  (panel always live)                               (memcpy -> native slot -> queue)
                                                                          |  background thread
                                                                          v
                                                    zero-copy DirectByteBuffer + cached JNI
                                                                          v
                                                    EgoEncoder.java (RGBA->NV12 mt + MediaCodec H.264)
                                                                          v
                                                                  internal.mp4

XRNode.Head + XRHandSubsystem ──> HandPoseLogger (background writer) ──> poses.txt
                              └──> HandJointViz (project to preview panel) ──> on-screen skeleton
```

### Components
- **`EgoCameraSource.cs`** — opens the RGB world-facing camera via the `XR_PICO_camera_image` extension (`PXR_CameraImage`), acquires an RGBA CPU buffer each frame, updates the preview texture, and **fans the frame out via an `OnFrame` event** so the preview and the recorder each get their own copy. Also reads camera **intrinsics (FOV)** and **extrinsics (offset)** for joint projection.
- **`EgoRecorder.cs`** — recording orchestration. The main thread only `memcpy`s the camera frame into a pooled native buffer and enqueues it. A background thread feeds frames to the Java encoder via a **shared `DirectByteBuffer` (zero-copy)** and a cached JNI method id. It also drives `HandPoseLogger`, writes `log.txt` + `frame_timestamps.txt`, and handles the volume-key toggle.
- **`EgoEncoder.java`** — `MediaCodec` H.264 + `MediaMuxer`. Receives a direct `ByteBuffer`, does a **multi-threaded RGBA→NV12** conversion (with vertical flip), and hardware-encodes to MP4.
- **`HandPoseLogger.cs`** — samples head (`XRNode.Head`) + both hands (`XRHandSubsystem`, 26 joints each) every frame and writes `poses.txt` on a background thread.
- **`HandJointViz.cs`** — projects the 26 joints of each hand onto the preview panel as **2D dots + skeleton bones** (left = green, right = blue), using camera FOV + extrinsic offset.
- **`EgoBootstrap.cs`** — `[RuntimeInitializeOnLoadMethod]` that assembles the rig, the world-space preview canvas, and the UI at startup.
- **`EgoActivity.java`** — a `UnityPlayerActivity` subclass that intercepts the volume keys and forwards them to Unity (`UnitySendMessage`).

## 3. Key technical problems solved

1. **Preview stays visible during recording.** The original EgoKit hides the live preview the moment recording starts (it routes the single camera stream to the encoder). Here the camera frame is **fanned out via an event**, so the preview texture and the recorder each receive an independent copy — the preview never disappears.

2. **5 fps → 60 fps (12×) via zero-copy.** Profiling showed the encode itself takes only ~5 ms/frame, but `AndroidJavaObject.Call` marshalling a 4.9 MB `byte[]` to Java took **~230 ms/frame**. Fix: write the frame into native memory wrapped by a **`DirectByteBuffer` (`AndroidJNI.NewDirectByteBuffer`)**, cache the `encodeFrame` method id, and call it with the low-level **`AndroidJNI.CallVoidMethod`** — the 4.9 MB no longer crosses the slow managed-array marshaller. Result: camera-limited 60 fps.

3. **Upright video.** The PICO camera buffer is **bottom-up**; the preview looks correct (Unity textures are bottom-up too) but the encoder produced an upside-down video. Fixed by reading source rows flipped (`height-1-j`) during NV12 conversion.

4. **Hand skeleton overlaid on the preview.** Joints are transformed to **head-relative** space, the camera **extrinsic offset** is subtracted (parallax), then projected through the camera **FOV** (intrinsics) onto the preview panel as dots + bones.

5. **Correct head pose under the PXR backend.** With the PXR (non-OpenXR) loader, the scene `Camera.main` transform does **not** follow the HMD, so head pose is read from `InputDevices.GetDeviceAtXRNode(XRNode.Head)`.

## 4. Build & setup

- **Unity 2022.3** (Tuanjie also works).
- Packages: **XR Hands** (`com.unity.xr.hands` 1.4.x, auto-downloaded from the Unity registry) + the **PICO Unity Integration SDK** (`com.unity.xr.picoxr`).
  - ⚠️ **The PICO SDK is NOT included in this repo** (third-party, license-restricted). Download it from the [PICO developer site](https://developer.picoxr.com/), then fix the path in `Packages/manifest.json` — it currently reads `"com.unity.xr.picoxr": "file:../../PICO-Unity-Integration-SDK"`; point it to your SDK folder, or remove that line and import the SDK via the Unity Package Manager.
- `Resources/PXR_ProjectSetting`: enable **Video See-Through** and **Hand Tracking**.
- Player Settings: enable **Allow 'unsafe' Code** (needed for `NewDirectByteBuffer`).
- `AndroidManifest.xml` declares `CAMERA` + `MANAGE_EXTERNAL_STORAGE`, and sets the launcher activity to `com.ego.EgoActivity`.
- Build target **Android**, deploy to the Pico 4 Ultra (Build And Run).
- On first run, grant the camera permission and **"All files access"** (so it can write to `DCIM/Recordings`). Via adb: `adb shell appops set <pkg> MANAGE_EXTERNAL_STORAGE allow`.

## 5. Usage

1. Put on the headset, launch **PicoEgoCapture**. A small first-person preview appears, with the hand skeleton drawn on it.
2. Press a **volume key** to start recording (red dot + timer appear); press again to stop.
3. Files land in `/sdcard/DCIM/Recordings/<timestamp>/` (visible over USB / MTP).
4. **Mirroring for a demo:** `scrcpy --crop=2160:2160:0:0 --no-audio` mirrors one eye to a PC.

## 6. Aligning video and poses

Video and poses run at different rates (video 60 fps, poses ~90 fps). To align:
- Video frame *i* time = `frame_timestamps.txt` line *i* (unix ms).
- Pose *j* time = `log.txt` poses "First frame unix ms" + that row's `t_ms`.
- For each video frame, pick the nearest pose by unix-ms timestamp.

## 7. Limitations

- Video is **60 fps of unique frames** (the camera hardware max). EgoKit reports ~89 fps but that includes duplicate frames encoded at the 90 Hz display rate.
- The on-screen skeleton overlay alignment is approximate (camera parallax / FOV).
- For training you still need: enough episodes, video↔pose time alignment, handling of untracked (zero) frames, and a coordinate-frame transform.

## License and acknowledgements

Independent clean-room reimplementation inspired by the published EgoKit paper and its observable data format; built on the PICO Unity Integration SDK, Unity XR Hands, and Android MediaCodec. No EgoKit source or binary was decompiled.

## Public source release

- This source snapshot has not been rebuilt or tested on a headset as part of the public release preparation. The original project used Unity 2022.3.62f3c1 and XR Hands 1.4.1. Camera availability and recording rate depend on the installed PICO SDK and headset firmware.
- Install the PICO Unity Integration SDK separately in a sibling directory named `PICO-Unity-Integration-SDK`, or update its local path in `Packages/manifest.json`. The exact SDK version used by the original build was not recorded in this snapshot.
- Vendor debugger prefabs are excluded. Import any required vendor debugger resources through your separately installed SDK; the collection scripts do not reference those prefabs.
- Original project code is provided under the MIT License; external SDKs remain under their own licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- Obtain participants' consent before collecting recordings. Videos and poses can contain identifying information.
