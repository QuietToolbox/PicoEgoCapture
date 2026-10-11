// EgoRecorder.cs — 录制编排:相机帧 -> 后台编码线程 -> internal.mp4;头+手 -> poses.txt。
// 提帧率关键:主线程只做"拷贝+入队"(快),RGBA->NV12+MediaCodec 编码全在后台线程,
// 不再拖累相机/预览主循环。预览贴图在 EgoCameraSource 里照常更新 = 录制时预览不消失。
// v1:autoTest=true 开流后自动录 5 秒。

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class EgoRecorder : MonoBehaviour
{
    public EgoCameraSource source;
    public GameObject recIndicator;   // 录制时显示的红点
    public UnityEngine.UI.Text statusText;  // 状态/计时文字
    public int bitrate = 10_000_000;
    public int targetFps = 30;

    [Header("自动测试:开流后自动录一段")]
    public bool autoTest = true;
    public float autoStartAfter = 4f;
    public float autoDuration = 5f;

    [DllImport("c", EntryPoint = "memcpy")] static extern IntPtr memcpy(IntPtr dest, IntPtr src, UIntPtr n);

    class FrameItem { public int slot; public long ptsUs; }

    const int SLOTS = 4;
    volatile bool _recording;
    BlockingCollection<FrameItem> _queue;
    IntPtr[] _natSlots;           // 原生内存(每帧 memcpy 进去)
    IntPtr[] _bbGlobal;           // 对应 DirectByteBuffer 的全局引用
    ConcurrentQueue<int> _free;   // 空闲 slot 索引
    volatile bool _slotsReady;
    Thread _worker;
    int _encoded;
    long _startNs = -1;
    int _frameSize;
    string _mp4Path;
    string _dir;
    System.DateTime _sessStart;
    long _vidFirstMs, _vidLastMs;
    readonly System.Collections.Generic.List<long> _vidFrameMs = new System.Collections.Generic.List<long>(); // 每帧 unix ms
    HandPoseLogger _hand;

    float _recStartTime, _stoppedUntil;
    float _t;
    bool _autoStarted, _autoStopped, _subscribed;

    void Start() { Subscribe(); }
    void OnEnable() { Subscribe(); }
    void Subscribe() { if (!_subscribed && source != null) { source.OnFrame += HandleFrame; _subscribed = true; } }
    void OnDisable() { if (_subscribed) { source.OnFrame -= HandleFrame; _subscribed = false; } if (_recording) StopRecording(); }

    void Update()
    {
        if (_recording && _hand != null && _hand.IsLogging)
            _hand.Sample((long)(Time.unscaledTimeAsDouble * 1000.0));

        UpdateStatus();

        if (!autoTest) return;
        _t += Time.deltaTime;
        if (!_autoStarted && _t > autoStartAfter && source != null && source.IsStreaming) { _autoStarted = true; StartRecording(); }
        if (_autoStarted && !_autoStopped && _t > autoStartAfter + autoDuration) { _autoStopped = true; StopRecording(); }
    }

    void UpdateStatus()
    {
        if (statusText == null) return;
        if (_recording)
        {
            int s = (int)(Time.time - _recStartTime);
            statusText.color = Color.red;
            statusText.text = $"REC  {s / 60:0}:{s % 60:00}   (Vol key: STOP)";
        }
        else if (Time.time < _stoppedUntil) { /* 保留绿色"已保存"提示 3 秒 */ }
        else { statusText.color = Color.white; statusText.text = "Vol key: START recording"; }
    }

    public void StartRecording()
    {
        if (_recording) return;
        try
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // 存到共享存储 DCIM/Recordings(USB 可见,和 EgoKit 一致;需 MANAGE_EXTERNAL_STORAGE)
            string dir = "/sdcard/DCIM/Recordings/" + ts;
            Directory.CreateDirectory(dir);
            _dir = dir; _sessStart = DateTime.Now; _vidFirstMs = 0; _vidLastMs = 0; _vidFrameMs.Clear();
            _mp4Path = dir + "/internal.mp4";
            int w = source.FrameWidth > 0 ? source.FrameWidth : source.desiredWidth;
            int h = source.FrameHeight > 0 ? source.FrameHeight : source.desiredHeight;
            _frameSize = w * h * 4;
            _startNs = -1; _encoded = 0; _slotsReady = false;
            _queue = new BlockingCollection<FrameItem>(boundedCapacity: SLOTS - 1);

            // 头 + 手 poses
            _hand = new HandPoseLogger();
            _hand.Begin(dir + "/poses.txt", null, null);

            _recording = true;
            _recStartTime = Time.time; _stoppedUntil = 0f;
            if (recIndicator != null) recIndicator.SetActive(true);
            _worker = new Thread(() => EncodeLoop(_mp4Path, w, h)) { IsBackground = true, Name = "EgoEncode" };
            _worker.Start();
            Debug.Log($"[EgoRecorder] start {w}x{h} -> {_mp4Path}");
        }
        catch (Exception e) { Debug.LogError("[EgoRecorder] start failed: " + e); _recording = false; }
    }

    // 后台线程:所有 JNI/编码操作都在这里
    void EncodeLoop(string path, int w, int h)
    {
        AndroidJNI.AttachCurrentThread();
        AndroidJavaObject encoder = null;
        try
        {
            encoder = new AndroidJavaObject("EgoEncoder");
            bool ok = encoder.Call<bool>("start", path, w, h, targetFps, bitrate);
            if (!ok) { Debug.LogError("[EgoRecorder] encoder.start returned false"); }

            // 缓存 encodeFrame(ByteBuffer,long) 的 methodID(避免每帧反射)
            IntPtr encObj = encoder.GetRawObject();
            IntPtr encClass = encoder.GetRawClass();
            IntPtr mid = AndroidJNI.GetMethodID(encClass, "encodeFrame", "(Ljava/nio/ByteBuffer;J)V");

            // 直接缓冲区池:原生内存 + DirectByteBuffer 全局引用(零拷贝传给 Java)
            _natSlots = new IntPtr[SLOTS];
            _bbGlobal = new IntPtr[SLOTS];
            _free = new ConcurrentQueue<int>();
            for (int i = 0; i < SLOTS; i++)
            {
                _natSlots[i] = Marshal.AllocHGlobal(_frameSize);
                IntPtr loc;
                unsafe { loc = AndroidJNI.NewDirectByteBuffer((byte*)_natSlots[i], (long)_frameSize); }
                _bbGlobal[i] = AndroidJNI.NewGlobalRef(loc);
                AndroidJNI.DeleteLocalRef(loc);
                _free.Enqueue(i);
            }
            _slotsReady = true;

            var args = new jvalue[2];
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                args[0].l = _bbGlobal[item.slot];
                args[1].j = item.ptsUs;
                try { AndroidJNI.CallVoidMethod(encObj, mid, args); _encoded++; }
                catch (Exception e) { Debug.LogError("[EgoRecorder] CallVoidMethod: " + e); }
                _free.Enqueue(item.slot);
            }
            encoder.Call("stop");
            Debug.Log($"[EgoRecorder] encoder done, {_encoded} frames -> {path}");
        }
        catch (Exception e) { Debug.LogError("[EgoRecorder] EncodeLoop: " + e); }
        finally
        {
            _slotsReady = false;
            if (_bbGlobal != null) for (int i = 0; i < _bbGlobal.Length; i++) if (_bbGlobal[i] != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(_bbGlobal[i]);
            if (_natSlots != null) for (int i = 0; i < _natSlots.Length; i++) if (_natSlots[i] != IntPtr.Zero) Marshal.FreeHGlobal(_natSlots[i]);
            _bbGlobal = null; _natSlots = null;
            encoder?.Dispose();
            AndroidJNI.DetachCurrentThread();
        }
    }

    // 主线程:memcpy 相机帧 → 原生 slot(原生→原生,极快),入队 slot 索引;满则丢帧
    void HandleFrame(IntPtr buffer, int size, int w, int h, int bpp, long captureNs)
    {
        if (!_recording || !_slotsReady || bpp != 4 || size != _frameSize) return;
        if (_startNs < 0) _startNs = captureNs;
        if (!_free.TryDequeue(out int slot)) return;          // 无空闲 slot → 丢帧
        memcpy(_natSlots[slot], buffer, (UIntPtr)(uint)size);
        var item = new FrameItem { slot = slot, ptsUs = (captureNs - _startNs) / 1000 };
        if (_queue == null || !_queue.TryAdd(item)) { _free.Enqueue(slot); return; } // 队列满 → 退回 slot
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_vidFirstMs == 0) _vidFirstMs = now;
        _vidLastMs = now;
        _vidFrameMs.Add(now);
    }

    // 被 EgoActivity 经 UnitySendMessage 调用(音量键开始/停止)
    public void OnVolumeKey(string _)
    {
        if (_recording) StopRecording(); else StartRecording();
    }

    public void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        if (recIndicator != null) recIndicator.SetActive(false);
        try { _queue.CompleteAdding(); } catch { }
        try { if (_worker != null && _worker.IsAlive) _worker.Join(5000); } catch { }   // 等后台收尾(finalize mp4)
        int poseCount = 0; long poseFirst = 0, poseLast = 0;
        try { if (_hand != null) { poseCount = _hand.End(); poseFirst = _hand.FirstUnixMs; poseLast = _hand.LastUnixMs; Debug.Log($"[EgoRecorder] poses {poseCount} frames"); } }
        catch (Exception e) { Debug.LogError("[EgoRecorder] poses end: " + e); }
        try { WriteLog(poseCount, poseFirst, poseLast); } catch (Exception e) { Debug.LogError("[EgoRecorder] log.txt: " + e); }
        try { WriteFrameTimestamps(); } catch (Exception e) { Debug.LogError("[EgoRecorder] frame_timestamps: " + e); }
        Debug.Log($"[EgoRecorder] stopped, {_encoded} video frames -> {_mp4Path}");
        _stoppedUntil = Time.time + 3f;
        if (statusText != null) { statusText.color = Color.green; statusText.text = $"Saved {_encoded} frames (mp4 + poses)"; }
        _hand = null; _worker = null;
        try { _queue?.Dispose(); } catch { } _queue = null;
    }

    // 写 log.txt(会话元数据),格式逐字对齐 EgoKit
    void WriteLog(int poseCount, long poseFirst, long poseLast)
    {
        if (string.IsNullOrEmpty(_dir)) return;
        string ts = Path.GetFileName(_dir);
        var sb = new System.Text.StringBuilder();
        sb.Append("Recording Session: ").Append(ts).Append('\n');
        sb.Append("Started:  ").Append(_sessStart.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\n');
        sb.Append("Ended:    ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\n');
        sb.Append("================================\n\n");
        sb.Append("File: internal.mp4\n");
        sb.Append("  Source: Internal Camera\n");
        sb.Append("  Total frames: ").Append(_encoded).Append('\n');
        sb.Append("  First frame timestamp (unix ms): ").Append(_vidFirstMs).Append('\n');
        sb.Append("  Last frame timestamp  (unix ms): ").Append(_vidLastMs).Append('\n');
        sb.Append("  Duration (ms): ").Append(_vidLastMs - _vidFirstMs).Append("\n\n");
        sb.Append("File: poses.txt\n");
        sb.Append("  Source: Poses\n");
        sb.Append("  Total frames: ").Append(poseCount).Append('\n');
        sb.Append("  First frame timestamp (unix ms): ").Append(poseFirst).Append('\n');
        sb.Append("  Last frame timestamp  (unix ms): ").Append(poseLast).Append('\n');
        sb.Append("  Duration (ms): ").Append(poseLast - poseFirst).Append('\n');
        File.WriteAllText(_dir + "/log.txt", sb.ToString());
        Debug.Log($"[EgoRecorder] log.txt written ({_encoded} vid / {poseCount} pose frames)");
    }

    // 逐帧时间戳:internal.mp4 第 i 帧的采集 unix ms,与 poses 同一时钟 → 可精确对齐
    void WriteFrameTimestamps()
    {
        if (string.IsNullOrEmpty(_dir) || _vidFrameMs.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("# internal.mp4 per-frame capture time; align to poses by unix ms\n");
        sb.Append("# idx unix_ms\n");
        for (int i = 0; i < _vidFrameMs.Count; i++)
            sb.Append(i).Append(' ').Append(_vidFrameMs[i]).Append('\n');
        File.WriteAllText(_dir + "/frame_timestamps.txt", sb.ToString());
        Debug.Log($"[EgoRecorder] frame_timestamps.txt written ({_vidFrameMs.Count} frames)");
    }
}
