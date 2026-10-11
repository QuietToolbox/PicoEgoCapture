# PicoEgoCapture

[English](README.md) | **简体中文**

<a href="https://quiettoolbox.github.io/"><img src="https://quiettoolbox.github.io/assets/quiettoolbox-avatar.jpg" width="32" height="32" alt="QuietToolbox"></a> **[← 工具箱首页](https://quiettoolbox.github.io/)**

由 [QuietToolbox](https://github.com/QuietToolbox) 维护。

面向 **PICO 4 Ultra** 头显的**第一视角(egocentric)数据采集** App:同步录制**第一视角视频 + 头部位姿 + 双手 26 关节 3D 位姿**,输出格式对齐 EgoKit,用于训练机器人操作 / VLA / 世界模型。

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

## 许可证与致谢

基于公开的 EgoKit 论文与可观察的数据格式,独立净室重实现;依赖 PICO Unity Integration SDK、Unity XR Hands、Android MediaCodec。未反编译 EgoKit 的任何源码或二进制。

## 公开源码说明

- 本公开源码版本尚未重新构建或连接头显验证。原工程使用 Unity 2022.3.62f3c1 和 XR Hands 1.4.1。实际相机能力和录制帧率取决于 PICO SDK 与头显系统。
- 请将 PICO Unity Integration SDK 单独安装到名为 `PICO-Unity-Integration-SDK` 的同级目录，或修改 `Packages/manifest.json` 中的本地路径。本源码快照未保留原构建所用 SDK 的准确版本。
- 本仓库不包含厂商调试器预制件。如有需要，请从单独安装的 SDK 导入；采集脚本没有引用这些预制件。本仓库不分发 SDK、录制数据或预编译 APK。
- 原创代码采用 [MIT 许可证](LICENSE)，外部 SDK 遵循各自的许可证。第三方依赖见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
- 采集前请取得参与者同意。视频和位姿数据可能包含可识别个人身份的信息。
