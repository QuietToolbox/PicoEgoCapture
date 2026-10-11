// HandPoseLogger.cs
// 头部 + 双手 26 关节(OpenXR 顺序)→ poses.txt,格式与 EgoKit 逐列一致。
// 用 com.unity.xr.hands(XRHandSubsystem,PICO 端由 PXR_HandSubSystem 提供)。
// 每帧 Sample() 入队,后台线程写盘,避免帧抖动。
//
// 一行 = idx t_ms  head_pos(3) head_rot(4)  left[26*(pos3+rot4)]  right[26*(pos3+rot4)]
// 未追踪关节 = "0 0 0 0 0 0 1"

using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;

public class HandPoseLogger
{
    const int JointCount = 26; // OpenXR XR_HAND_JOINT_*_EXT: 0=Palm..25=LittleTip

    XRHandSubsystem _hands;
    Transform _head;            // 头部参考(通常是 XR 相机)
    Transform _trackingSpace;   // XR Origin;若设置则所有 pose 转到它的本地坐标(与 EgoKit 一致)

    StreamWriter _writer;
    BlockingCollection<string> _queue;
    Thread _writerThread;
    long _startMs = -1;
    int _idx;

    public bool IsLogging { get; private set; }
    public long FirstUnixMs { get; private set; }   // 首帧 unix ms(写 log.txt 用)
    public long LastUnixMs { get; private set; }    // 末帧 unix ms

    public void Begin(string posesPath, Transform head, Transform trackingSpace)
    {
        _head = head; _trackingSpace = trackingSpace;
        _hands = GetHandSubsystem();
        if (_hands == null) Debug.LogWarning("[HandPoseLogger] XRHandSubsystem not found — hands will be zeros");

        _writer = new StreamWriter(posesPath, false, Encoding.ASCII);
        _writer.WriteLine("# idx t_ms head_pos(x y z) head_rot(x y z w) left[26]:(pos+rot) right[26]:(pos+rot)");
        _writer.WriteLine("# joints in OpenXR XR_HAND_JOINT order: 0=Palm 1=Wrist 2..5=Thumb(M,P,D,T) 6..10=Index(M,P,I,D,T) 11..15=Middle 16..20=Ring 21..25=Little");

        _queue = new BlockingCollection<string>(4096);
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "PoseWriter" };
        _writerThread.Start();
        _startMs = -1; _idx = 0;
        IsLogging = true;
    }

    /// <summary>每帧(主线程)调用,timestampMs 用相机帧时间戳(ns→ms)或 Time。</summary>
    public void Sample(long timestampMs)
    {
        if (!IsLogging) return;
        if (_startMs < 0) _startMs = timestampMs;
        long u = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (FirstUnixMs == 0) FirstUnixMs = u;
        LastUnixMs = u;

        var sb = new StringBuilder(2048);
        sb.Append(_idx++).Append(' ').Append(timestampMs - _startMs);

        // 头部:从 HMD 节点取(PXR 后端下场景相机 transform 不跟随头显)
        Vector3 hp = Vector3.zero; Quaternion hr = Quaternion.identity;
        var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        hmd.TryGetFeatureValue(CommonUsages.devicePosition, out hp);
        hmd.TryGetFeatureValue(CommonUsages.deviceRotation, out hr);
        AppendPose(sb, true, hp, hr);

        // 左右手
        AppendHand(sb, _hands != null && _hands.leftHand.isTracked ? _hands.leftHand : default, _hands != null && _hands.leftHand.isTracked);
        AppendHand(sb, _hands != null && _hands.rightHand.isTracked ? _hands.rightHand : default, _hands != null && _hands.rightHand.isTracked);

        _queue.TryAdd(sb.ToString());
    }

    void AppendHand(StringBuilder sb, XRHand hand, bool tracked)
    {
        for (int i = 0; i < JointCount; i++)
        {
            if (tracked)
            {
                var joint = hand.GetJoint(XRHandJointIDUtility.FromIndex(i));
                if (joint.TryGetPose(out Pose p))
                {
                    GetWorldToSpace(p.position, p.rotation, out var jp, out var jr);
                    AppendPose(sb, false, jp, jr);
                    continue;
                }
            }
            sb.Append(" 0 0 0 0 0 0 1"); // 未追踪/无效
        }
    }

    void GetWorldToSpace(Vector3 wpos, Quaternion wrot, out Vector3 pos, out Quaternion rot)
    {
        if (_trackingSpace != null)
        {
            pos = _trackingSpace.InverseTransformPoint(wpos);
            rot = Quaternion.Inverse(_trackingSpace.rotation) * wrot;
        }
        else { pos = wpos; rot = wrot; }
    }

    static void AppendPose(StringBuilder sb, bool leadingSpaceOptional, Vector3 p, Quaternion r)
    {
        var c = CultureInfo.InvariantCulture;
        sb.Append(' ').Append(p.x.ToString("R", c)).Append(' ').Append(p.y.ToString("R", c)).Append(' ').Append(p.z.ToString("R", c));
        sb.Append(' ').Append(r.x.ToString("R", c)).Append(' ').Append(r.y.ToString("R", c)).Append(' ').Append(r.z.ToString("R", c)).Append(' ').Append(r.w.ToString("R", c));
    }

    void WriterLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
            _writer.WriteLine(line);
    }

    public int End()
    {
        if (!IsLogging) return 0;
        IsLogging = false;
        _queue.CompleteAdding();
        _writerThread.Join(2000);
        _writer.Flush(); _writer.Dispose();
        return _idx;
    }

    static XRHandSubsystem GetHandSubsystem()
    {
        var subs = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subs);
        return subs.Count > 0 ? subs[0] : null;
    }
}
