// EgoCameraSource.cs
// Pico 4 Ultra 第一视角相机取帧(PXR_CameraImage / XR_PICO_camera_image 扩展,无需企业授权)。
// 核心:每帧拿到 CPU RGBA 字节后,①更新预览贴图 ②通过 OnFrame 扇出给录制器。
// 这样预览和录制各拿一份,互不抢占 —— 根治 EgoKit "录制时预览消失" 的缺陷。
//
// 依赖:PICO Unity Integration SDK(Unity.XR.PXR),并在 XR Plug-in Management 启用 PICO。
// 挂载:场景里一个 GameObject,Inspector 里把 previewImage 指到预览面板的 RawImage。

using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.UI;

public class EgoCameraSource : MonoBehaviour
{
    [Header("预览(录制时也会持续更新)")]
    public RawImage previewImage;

    [Header("期望参数(会自动匹配设备支持的最接近值)")]
    public int desiredWidth = 1280;
    public int desiredHeight = 960;
    public int desiredFps = 60;

    /// <summary>当前帧贴图(RGBA/RGB)。</summary>
    public Texture2D Frame { get; private set; }
    public bool IsStreaming { get; private set; }
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public Vector2 CameraFovDeg { get; private set; }   // 相机水平/垂直 FOV(度),用于把关节投影到预览
    public Vector3 CameraPosOffset { get; private set; } // 相机相对头显的位置偏移(Unity 坐标),修正预览投影视差

    /// <summary>
    /// 每帧回调(主线程)。参数:原始像素指针、字节数、宽、高、每像素字节、采集时间戳(ns)。
    /// 注意:buffer 仅在本次回调内有效(回调返回后会 ReleaseCameraImage),录制器必须同步拷走。
    /// </summary>
    public event Action<IntPtr, int, int, int, int, long> OnFrame;

    XrCameraIdPICO _cameraId;
    bool _deviceReady;
    long _lastCaptureTime;
    CancellationTokenSource _cts;

    async void Start()
    {
        _cts = new CancellationTokenSource();
        // 透视相机必须开启,否则 RGB 相机流不可用
        PXR_Manager.EnableVideoSeeThrough = true;
        try
        {
            await InitCameraAsync(_cts.Token);
        }
        catch (Exception e)
        {
            Debug.LogError($"[EgoCameraSource] init failed: {e}");
        }
    }

    async Task InitCameraAsync(CancellationToken token)
    {
        if (PXR_CameraImage.GetAvailableCameras(out XrCameraIdPICO[] cams) != PxrResult.SUCCESS || cams == null || cams.Length == 0)
        {
            Debug.LogError("[EgoCameraSource] no cameras available");
            return;
        }

        // 选 world-facing(前向)相机
        _cameraId = cams[0];
        foreach (var c in cams)
        {
            if (PXR_CameraImage.GetCameraFacingProperties(c, out var facing) == PxrResult.SUCCESS
                && facing == XrCameraFacingPICO.XR_CAMERA_FACING_WORLD_PICO)
            {
                _cameraId = c;
                break;
            }
        }

        // 查询能力,挑最接近期望的分辨率/帧率,以及第一个可用的 format/transfer/model
        PXR_CameraImage.GetCameraImageResolutionCapability(_cameraId, out PxrExtent2Di[] resolutions);
        PXR_CameraImage.GetCameraImageFpsCapability(_cameraId, out XrCameraImageFpsPICO[] fpsList);
        PXR_CameraImage.GetCameraImageFormatCapability(_cameraId, out XrCameraImageFormatPICO[] formats);
        PXR_CameraImage.GetCameraDataTransferTypeCapability(_cameraId, out XrCameraDataTransferTypePICO[] transfers);
        PXR_CameraImage.GetCameraCameraModelCapability(_cameraId, out XrCameraModelPICO[] models);

        var res = PickResolution(resolutions, desiredWidth, desiredHeight);
        var fps = PickFps(fpsList, desiredFps);
        var format = (formats != null && formats.Length > 0) ? formats[0] : default;
        var transfer = (transfers != null && transfers.Length > 0) ? transfers[0] : default;
        var model = (models != null && models.Length > 0) ? models[0] : default;
        Debug.Log($"[EgoCameraSource] cam={_cameraId} res={res.width}x{res.height} fps={fps} fmt={format} xfer={transfer} model={model}");

        if (await PXR_CameraImage.CreateCameraDeviceAsync(_cameraId, token) != PxrResult.SUCCESS)
        {
            Debug.LogError("[EgoCameraSource] CreateCameraDevice failed");
            return;
        }
        if (await PXR_CameraImage.CreateCameraCaptureSessionAsync(_cameraId, res.width, res.height, fps, format, transfer, model, token) != PxrResult.SUCCESS)
        {
            Debug.LogError("[EgoCameraSource] CreateCameraCaptureSession failed");
            return;
        }
        if (PXR_CameraImage.BeginCameraCapture(_cameraId) != PxrResult.SUCCESS)
        {
            Debug.LogError("[EgoCameraSource] BeginCameraCapture failed");
            return;
        }

        // 取相机内参 FOV(度),供预览投影手部关节用
        if (PXR_CameraImage.GetCameraIntrinsics(_cameraId, out XrCameraIntrinsics intr) == PxrResult.SUCCESS)
        {
            CameraFovDeg = new Vector2(intr.fov.X, intr.fov.Y);
            Debug.Log($"[EgoCameraSource] fov={intr.fov.X:F1}x{intr.fov.Y:F1}deg focal=({intr.focalLength.X:F1},{intr.focalLength.Y:F1}) pp=({intr.principalPoint.X:F1},{intr.principalPoint.Y:F1})");
        }
        // 取相机外参(相对头显的位姿)→ 位置偏移,用于修正预览投影视差(z 取反:OpenXR 右手→Unity 左手)
        if (PXR_CameraImage.GetCameraExtrinsics(_cameraId, out XrCameraExtrinsics ext) == PxrResult.SUCCESS)
        {
            var pos = ext.pose.Position; var ori = ext.pose.Orientation;
            CameraPosOffset = new Vector3(pos.X, pos.Y, -pos.Z);
            Debug.Log($"[EgoCameraSource] extrinsics pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) ori=({ori.X:F3},{ori.Y:F3},{ori.Z:F3},{ori.W:F3})");
        }

        _deviceReady = true;
        IsStreaming = true;
        Debug.Log("[EgoCameraSource] streaming started");
    }

    void Update()
    {
        if (!_deviceReady) return;

        // 取最新一帧(比 _lastCaptureTime 更新的)
        if (PXR_CameraImage.AcquireCameraImage(_cameraId, _lastCaptureTime, out ulong imageId, out long captureTime) != PxrResult.SUCCESS)
            return; // 无新帧(XR_CAMERA_IMAGE_NO_UPDATE)或暂不可用
        _lastCaptureTime = captureTime;

        if (PXR_CameraImage.GetCameraImageData(_cameraId, imageId, out XrCameraImageDataRawBuffer raw) == PxrResult.SUCCESS
            && raw.buffer != IntPtr.Zero && raw.bufferSize > 0)
        {
            EnsureTexture((int)raw.width, (int)raw.height, (int)raw.bytesPerPixel);
            Frame.LoadRawTextureData(raw.buffer, (int)raw.bufferSize);
            Frame.Apply(false);
            if (previewImage != null) previewImage.texture = Frame;   // ① 预览(始终更新,录制中也一样)
            OnFrame?.Invoke(raw.buffer, (int)raw.bufferSize, (int)raw.width, (int)raw.height, (int)raw.bytesPerPixel, captureTime); // ② 录制器
        }

        PXR_CameraImage.ReleaseCameraImage(_cameraId, imageId);
    }

    void EnsureTexture(int w, int h, int bpp)
    {
        var fmt = bpp == 3 ? TextureFormat.RGB24 : TextureFormat.RGBA32;
        if (Frame == null || FrameWidth != w || FrameHeight != h || Frame.format != fmt)
        {
            Frame = new Texture2D(w, h, fmt, false);
            FrameWidth = w; FrameHeight = h;
        }
    }

    static PxrExtent2Di PickResolution(PxrExtent2Di[] list, int w, int h)
    {
        if (list == null || list.Length == 0) return new PxrExtent2Di { width = w, height = h };
        PxrExtent2Di best = list[0]; long bestD = long.MaxValue;
        foreach (var r in list)
        {
            long d = (long)(r.width - w) * (r.width - w) + (long)(r.height - h) * (r.height - h);
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    static XrCameraImageFpsPICO PickFps(XrCameraImageFpsPICO[] list, int fps)
    {
        if (list == null || list.Length == 0) return (XrCameraImageFpsPICO)fps;
        XrCameraImageFpsPICO best = list[0]; int bestD = int.MaxValue;
        foreach (var f in list)
        {
            int d = Mathf.Abs((int)f - fps);
            if (d < bestD) { bestD = d; best = f; }
        }
        return best;
    }

    void OnDestroy()
    {
        _cts?.Cancel();
        if (_deviceReady)
        {
            PXR_CameraImage.EndCameraCapture(_cameraId);
            PXR_CameraImage.DestroyCameraCaptureSession(_cameraId);
            PXR_CameraImage.DestroyCameraDevice(_cameraId);
            _deviceReady = false;
            IsStreaming = false;
        }
    }
}
