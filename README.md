# PicoEgoCapture

<a href="https://quiettoolbox.github.io/"><img src="https://quiettoolbox.github.io/assets/quiettoolbox-avatar.jpg" width="32" height="32" alt="QuietToolbox"></a> **[← QuietToolbox 首页 / Toolbox Home](https://quiettoolbox.github.io/)**

Maintained by [QuietToolbox](https://github.com/QuietToolbox).

First-person (egocentric) data collection app for the **PICO 4 Ultra** headset: records a synchronized **first-person video + head pose + both hands' 26-joint 3D poses**, in a format compatible with EgoKit, for training robot manipulation / VLA / world models.

面向 **PICO 4 Ultra** 头显的**第一视角(egocentric)数据采集** App:同步录制**第一视角视频 + 头部位姿 + 双手 26 关节 3D 位姿**,输出格式对齐 EgoKit,用于训练机器人操作 / VLA / 世界模型。

> Languages: **[English](#english)** | **[中文](#中文)**

---

<a name="english"></a>
# English

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

- **Unity 2022.3** (Tuanjie / 团结引擎 also works).
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

---

<a name="中文"></a>
# 中文

## 两个版本(同一套代码,两种打包)

本项目以两个 App 形式提供,可在头显里**并存**(包名不同):

| App | 包名 | 用途 | 预览 |
|---|---|---|---|
| **PicoEgoCapture** | `com.DefaultCompany.EgoRepro` | **采集数据** —— 戴着录制示范 | HUD 预览,不挡真实视野 |
| **PicoEgoView** | `com.DefaultCompany.PicoEgoView` | **演示 / 投屏** —— 把第一视角 + 手部骨架投到电脑/大屏(如汇报) | 大画面,铺满视野 |

两者同一套代码,仅**名字 + 包名**和**预览大小**不同(PicoEgoCapture 用约 1 米处的小 HUD 面板;PicoEgoView 铺满视野)。**`main`** 分支 = PicoEgoCapture;**`picoegoview`** 分支 = PicoEgoView。包名不同,装一个不会覆盖另一个。

**想(重新)build 某个版本:**
1. 切分支:`git checkout main` = PicoEgoCapture,`git checkout picoegoview` = PicoEgoView;
2. 关掉 Unity 工程再重新打开,让它读到该分支的名字/包名(Unity 不一定实时更新这些设置);
3. `File` → `Build And Run`。

不切分支直接 build,得到的是当前分支(默认 `main` = PicoEgoCapture)。两个 App 在头显里并存,build 一个不会删掉另一个。

## 快速上手

### 源码发布

本仓库当前仅提供源码，尚未提供经过核对的公开 APK。请按下面步骤单独安装 PICO SDK，并用 Unity 构建。

### 从源码构建

假设你完全没用过 Unity,逐步来:

1. **装 Unity Hub**:<https://unity.com/download>。
2. Unity Hub → **Installs → Install Editor → Unity 2022.3.x(LTS)**。在 **"Add modules"** 那步**必须**勾选 **Android Build Support**,并展开它再勾上 **OpenJDK** 和 **Android SDK & NDK Tools**(不勾这些就没法给头显打包)。
3. **拿 PICO SDK**:到 <https://developer.picoxr.com/> 注册开发者账号,下载 **PICO Unity Integration SDK**,解压到某个目录,例如 `C:/pico-sdk/PICO-Unity-Integration-SDK`。
4. **克隆本仓库**。用文本编辑器打开 `Packages/manifest.json`,把这一行
   `"com.unity.xr.picoxr": "file:../../PICO-Unity-Integration-SDK",`
   改成**你自己**的 SDK 路径(用正斜杠),例如 `"file:C:/pico-sdk/PICO-Unity-Integration-SDK"`。
5. Unity Hub → **Projects → Add**,选中克隆下来的文件夹,用 **2022.3.x** 打开。首次导入要几分钟。
6. 如果弹出 **PICO 工程配置窗口**,点 **"Apply All"**(它会设好 Android + ARM64 + IL2CPP 并启用 PICO XR 插件)。
7. 连上 Pico 4 Ultra(开启开发者模式 + USB 调试;在头显里同意提示)。
8. **File → Build Settings**:平台切到 **Android**(需要就点 *Switch Platform*),然后点 **Build And Run**。
9. 头显里首次运行:**允许相机**,再授予**所有文件访问**(`adb shell appops set com.DefaultCompany.EgoRepro MANAGE_EXTERNAL_STORAGE allow`,或 设置 → 应用 → PicoEgoCapture → 权限)。

**常见问题**
- *点了 Build 没反应 / 还报旧错*:这个 Unity 版本有时不重编译外部改动 —— **关掉工程重新打开**强制重编译,并确认输出 APK 的时间戳更新了。
- *"JDK not found"*:打开一次 **Edit → Preferences → External Tools**,让 Unity 重新识别自带的 JDK / SDK / NDK。
- *Gradle 下载卡住(中国大陆网络)*:构建前在 `~/.gradle/` 配代理 + 国内镜像,或挂 VPN。

## 1. 输出的数据

每次录制在 `/sdcard/DCIM/Recordings/<yyyyMMdd_HHmmss>/` 下生成**四个文件**:

| 文件 | 内容 |
|---|---|
| `internal.mp4` | 第一视角视频,H.264,**1280×960 @ 60fps**(正向、原始相机画面、无叠加) |
| `poses.txt` | 每帧:头部位姿 + 双手各 26 关节(3D 位置 + 旋转),**373 列**,OpenXR 关节顺序 |
| `log.txt` | 会话元数据:起止时间、各文件帧数、首/末帧 unix-ms 时间戳、时长(对齐 EgoKit) |
| `frame_timestamps.txt` | 视频逐帧 unix-ms 采集时间,用于视频↔姿态精确对齐(我们新增;EgoKit 只存首/末帧) |

**`poses.txt` 每行格式**(空格分隔,373 字段):
```
idx  t_ms  头_px 头_py 头_pz  头_qx 头_qy 头_qz 头_qw \
     左0_px 左0_py 左0_pz 左0_qx 左0_qy 左0_qz 左0_qw ... 左25_(7) \
     右0_(7) ... 右25_(7)
```
- `idx` 帧号,`t_ms` 相对首帧的毫秒。
- 头部:3 位置 + 4 四元数。
- 左手:26 关节 ×(3 位置 + 4 四元数)= 182;右手同样 182。
- 未追踪的关节 → `0 0 0 0 0 0 1`。
- 关节顺序(OpenXR `XR_HAND_JOINT_*`):`0=手掌 1=手腕 2..5=拇指 6..10=食指 11..15=中指 16..20=无名 21..25=小指`。

## 2. 架构

一切由 `EgoBootstrap` 在运行时自动搭建(无需手动布场景)。数据流:

```
PICO RGB 相机 ──(PXR_CameraImage,RGBA CPU 缓冲)──> EgoCameraSource.Update()
                                                          |
                       +------------- OnFrame 事件 --------+---------------+
                       v                                                   v
                预览 Texture2D                                    EgoRecorder.HandleFrame
                (面板始终实时)                                   (memcpy -> 原生 slot -> 队列)
                                                                      |  后台线程
                                                                      v
                                              零拷贝 DirectByteBuffer + 缓存 JNI methodID
                                                                      v
                                              EgoEncoder.java(多线程 RGBA->NV12 + MediaCodec H.264)
                                                                      v
                                                              internal.mp4

XRNode.Head + XRHandSubsystem ──> HandPoseLogger(后台写盘) ──> poses.txt
                              └──> HandJointViz(投影到预览面板) ──> 屏上骨架
```

### 各模块
- **`EgoCameraSource.cs`** — 通过 `XR_PICO_camera_image` 扩展(`PXR_CameraImage`)打开前向 RGB 相机,每帧取得 RGBA CPU 缓冲,更新预览贴图,并**通过 `OnFrame` 事件把帧扇出**,让预览和录制器各拿一份。同时读取相机**内参(FOV)**和**外参(偏移)**用于关节投影。
- **`EgoRecorder.cs`** — 录制编排。主线程只做"把相机帧 `memcpy` 进池化的原生缓冲并入队"。后台线程用**共享 `DirectByteBuffer`(零拷贝)**+ 缓存的 JNI methodID 把帧喂给 Java 编码器。同时驱动 `HandPoseLogger`、写 `log.txt` + `frame_timestamps.txt`、处理音量键开关。
- **`EgoEncoder.java`** — `MediaCodec` H.264 + `MediaMuxer`。接收直接 `ByteBuffer`,做**多线程 RGBA→NV12**(含垂直翻转),硬件编码成 MP4。
- **`HandPoseLogger.cs`** — 每帧采样头部(`XRNode.Head`)+ 双手(`XRHandSubsystem`,各 26 关节),后台线程写 `poses.txt`。
- **`HandJointViz.cs`** — 把每只手 26 关节投影到预览面板,画成 **2D 关节点 + 骨架连线**(左手绿、右手蓝),用相机 FOV + 外参偏移。
- **`EgoBootstrap.cs`** — `[RuntimeInitializeOnLoadMethod]`,启动时搭好相机绑定、世界空间预览画布和 UI。
- **`EgoActivity.java`** — 继承 `UnityPlayerActivity`,拦截音量键并转发给 Unity(`UnitySendMessage`)。

## 3. 解决的关键技术问题

1. **录制时预览不消失。** 原版 EgoKit 一按录制就把预览隐藏(它把唯一的相机流切给编码器)。这里相机帧**通过事件扇出**,预览贴图和录制器各收到独立的一份,预览永不消失。

2. **5fps → 60fps(12 倍),靠零拷贝。** 实测编码本身只要 ~5ms/帧,但 `AndroidJavaObject.Call` 把 4.9MB `byte[]` 封送到 Java 要 **~230ms/帧**。解决:把帧写进由 **`DirectByteBuffer`(`AndroidJNI.NewDirectByteBuffer`)** 包裹的原生内存,缓存 `encodeFrame` 的 methodID,用底层 **`AndroidJNI.CallVoidMethod`** 调用 —— 那 4.9MB 不再走慢速的托管数组封送。结果:跑满相机上限 60fps。

3. **视频正向。** PICO 相机缓冲是**自下而上**;预览看着正常(Unity 贴图也是自下而上),但编码出来上下颠倒。解决:NV12 转换时按 `height-1-j` 翻转读取源行。

4. **手部骨架叠加到预览。** 关节先转到**相对头部**空间,减去相机**外参偏移**(视差),再用相机 **FOV**(内参)投影到预览面板,画成点 + 连线。

5. **PXR 后端下正确的头部位姿。** PXR(非 OpenXR)加载器下,场景 `Camera.main` 的 transform **不跟随**头显,所以头部位姿改从 `InputDevices.GetDeviceAtXRNode(XRNode.Head)` 读。

## 4. 构建与配置

- **Unity 2022.3**(团结引擎 / Tuanjie 也可)。
- 包:**XR Hands**(`com.unity.xr.hands` 1.4.x,从 Unity registry 自动下载)+ **PICO Unity Integration SDK**(`com.unity.xr.picoxr`)。
  - ⚠️ **PICO SDK 不在本仓库内**(第三方、有许可限制)。请到 [PICO 开发者官网](https://developer.picoxr.com/)下载,然后修改 `Packages/manifest.json` 里的路径 —— 当前是 `"com.unity.xr.picoxr": "file:../../PICO-Unity-Integration-SDK"`,改成你自己的 SDK 目录,或删掉该行用 Unity Package Manager 导入。
- `Resources/PXR_ProjectSetting`:开启 **Video See-Through** 和 **Hand Tracking**。
- Player Settings:勾选 **Allow 'unsafe' Code**(`NewDirectByteBuffer` 需要)。
- `AndroidManifest.xml` 声明 `CAMERA` + `MANAGE_EXTERNAL_STORAGE`,并把启动 Activity 设为 `com.ego.EgoActivity`。
- 目标平台 **Android**,Build And Run 部署到 Pico 4 Ultra。
- 首次运行授予相机权限和**"所有文件访问"**(才能写 `DCIM/Recordings`)。adb:`adb shell appops set <包名> MANAGE_EXTERNAL_STORAGE allow`。

## 5. 使用

1. 戴上头显,启动 **PicoEgoCapture**。显示小型第一视角预览面板,手上叠加骨架。
2. 按**音量键**开始录制(出现红点 + 计时);再按一次停止。
3. 文件存到 `/sdcard/DCIM/Recordings/<时间戳>/`(USB / MTP 可见)。
4. **投屏演示:** `scrcpy --crop=2160:2160:0:0 --no-audio` 把单眼镜像到电脑。

## 6. 视频与姿态对齐

视频和姿态帧率不同(视频 60fps、姿态 ~90fps)。对齐方法:
- 视频第 *i* 帧时间 = `frame_timestamps.txt` 第 *i* 行(unix ms)。
- 第 *j* 个姿态时间 = `log.txt` 中 poses 的 "First frame unix ms" + 该行 `t_ms`。
- 每个视频帧取 unix-ms 最近的姿态。

## 7. 局限

- 视频是 **60fps 的"真帧"**(相机硬件上限)。EgoKit 标称 ~89fps,但那含按 90Hz 刷新率编码的重复帧。
- 屏上骨架叠加的对齐是近似(相机视差 / FOV)。
- 用于训练还需要:足够多的片段、视频↔姿态时间对齐、对未追踪(全 0)帧的处理、以及坐标系变换。

---

## License / Acknowledgements

Independent clean-room reimplementation inspired by the published EgoKit paper and its observable data format; built on the PICO Unity Integration SDK, Unity XR Hands, and Android MediaCodec. No EgoKit source or binary was decompiled.

基于公开的 EgoKit 论文与可观察的数据格式,独立净室重实现;依赖 PICO Unity Integration SDK、Unity XR Hands、Android MediaCodec。未反编译 EgoKit 的任何源码或二进制。

## Public source release / 公开源码说明

- This source snapshot has not been rebuilt or tested on a headset as part of the public release preparation. The original project used Unity 2022.3.62f3c1 and XR Hands 1.4.1. Camera availability and recording rate depend on the installed PICO SDK and headset firmware.
- Install the PICO Unity Integration SDK separately in a sibling directory named `PICO-Unity-Integration-SDK`, or update its local path in `Packages/manifest.json`. The exact SDK version used by the original build was not recorded in this snapshot.
- Vendor debugger prefabs are excluded. Import any required vendor debugger resources through your separately installed SDK; the collection scripts do not reference those prefabs.
- Original project code is provided under the MIT License; external SDKs remain under their own licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- Obtain participants' consent before collecting recordings. Videos and poses can contain identifying information.

本公开源码版本尚未重新构建或连接头显验证。原工程使用 Unity 2022.3.62f3c1、XR Hands 1.4.1，原构建所用 PICO SDK 的准确版本未保留。实际相机能力和帧率取决于 SDK 与头显系统。请单独安装 PICO SDK；本仓库不分发厂商 SDK、调试器预制件、录制数据或预编译 APK。采集前请取得参与者同意。
