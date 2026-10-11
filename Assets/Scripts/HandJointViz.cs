// HandJointViz.cs — 把双手 26 关节按相机内/外参投影到预览面板,画成"关节点 + 骨架连线"(像 EgoKit)。
// 左手绿、右手蓝。关节相对头部 → 减相机偏移(相对相机)→ 针孔投影 → 面板 UV。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

public class HandJointViz : MonoBehaviour
{
    const int N = 26;
    // OpenXR 关节骨架(parent,child):腕→各掌骨,再各指逐节
    static readonly int[] Bones = {
        1,2, 2,3, 3,4, 4,5,              // 拇指
        1,6, 6,7, 7,8, 8,9, 9,10,        // 食指
        1,11, 11,12, 12,13, 13,14, 14,15,// 中指
        1,16, 16,17, 17,18, 18,19, 19,20,// 无名
        1,21, 21,22, 22,23, 23,24, 24,25 // 小指
    };

    public Transform head;
    public RectTransform previewCanvas;
    public RawImage previewImage;
    public EgoCameraSource cameraSource;
    public float dotSize = 8f, boneWidth = 4f;
    public bool flipV = false, flipH = false;

    XRHandSubsystem _hands;
    HandVis _vl, _vr;
    Vector2 _panel = new Vector2(640, 480);
    Vector2 _tanHalfFov = new Vector2(Mathf.Tan(32f * Mathf.Deg2Rad), Mathf.Tan(25f * Mathf.Deg2Rad));

    class HandVis
    {
        public RectTransform[] dots;
        public RectTransform[] bones;
        public Vector2[] pos;
        public bool[] vis;
    }

    void Start()
    {
        _hands = GetSub();
        if (previewCanvas != null) _panel = previewCanvas.sizeDelta;
        _vl = MakeHand(new Color(0.25f, 1f, 0.4f));   // 左手绿
        _vr = MakeHand(new Color(0.3f, 0.55f, 1f));   // 右手蓝
    }

    void TryFov()
    {
        if (cameraSource != null && cameraSource.CameraFovDeg.x > 1f)
        {
            var f = cameraSource.CameraFovDeg;
            _tanHalfFov = new Vector2(Mathf.Tan(f.x * 0.5f * Mathf.Deg2Rad), Mathf.Tan(f.y * 0.5f * Mathf.Deg2Rad));
        }
    }

    HandVis MakeHand(Color c)
    {
        var hv = new HandVis { dots = new RectTransform[N], bones = new RectTransform[Bones.Length / 2], pos = new Vector2[N], vis = new bool[N] };
        for (int b = 0; b < hv.bones.Length; b++) hv.bones[b] = MakeQuad(c * 0.85f, 1f, boneWidth); // 骨架先建(在底层)
        for (int i = 0; i < N; i++) hv.dots[i] = MakeQuad(c, dotSize, dotSize);                     // 关节点在上层
        return hv;
    }

    RectTransform MakeQuad(Color c, float w, float h)
    {
        var go = new GameObject("v", typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        if (previewCanvas != null) rt.SetParent(previewCanvas, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        go.GetComponent<Image>().color = c;
        go.SetActive(false);
        return rt;
    }

    void Update()
    {
        if (_hands == null) { _hands = GetSub(); if (_hands == null) return; }
        TryFov();

        var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        Vector3 hp = Vector3.zero; Quaternion hr = Quaternion.identity;
        hmd.TryGetFeatureValue(CommonUsages.devicePosition, out hp);
        hmd.TryGetFeatureValue(CommonUsages.deviceRotation, out hr);
        var hrInv = Quaternion.Inverse(hr);
        Vector3 camOff = cameraSource != null ? cameraSource.CameraPosOffset : Vector3.zero;

        UpdateHand(_hands.leftHand, _vl, hp, hrInv, camOff);
        UpdateHand(_hands.rightHand, _vr, hp, hrInv, camOff);
    }

    void UpdateHand(XRHand hand, HandVis hv, Vector3 hp, Quaternion hrInv, Vector3 camOff)
    {
        bool tr = hand.isTracked;
        for (int i = 0; i < N; i++)
        {
            hv.vis[i] = false;
            if (tr && hand.GetJoint(XRHandJointIDUtility.FromIndex(i)).TryGetPose(out Pose p))
            {
                Vector3 rel = hrInv * (p.position - hp) - camOff;   // 相对相机
                if (rel.z > 0.05f)
                {
                    float ndcX = (rel.x / rel.z) / _tanHalfFov.x;
                    float ndcY = (rel.y / rel.z) / _tanHalfFov.y;
                    if (flipH) ndcX = -ndcX;
                    if (flipV) ndcY = -ndcY;
                    if (Mathf.Abs(ndcX) <= 1f && Mathf.Abs(ndcY) <= 1f)
                    {
                        hv.pos[i] = new Vector2(ndcX * _panel.x * 0.5f, ndcY * _panel.y * 0.5f);
                        hv.vis[i] = true;
                    }
                }
            }
            if (hv.dots[i].gameObject.activeSelf != hv.vis[i]) hv.dots[i].gameObject.SetActive(hv.vis[i]);
            if (hv.vis[i]) hv.dots[i].anchoredPosition = hv.pos[i];
        }

        for (int b = 0; b < hv.bones.Length; b++)
        {
            int a = Bones[b * 2], c = Bones[b * 2 + 1];
            bool on = hv.vis[a] && hv.vis[c];
            var brt = hv.bones[b];
            if (brt.gameObject.activeSelf != on) brt.gameObject.SetActive(on);
            if (on)
            {
                Vector2 pa = hv.pos[a], pc = hv.pos[c], d = pc - pa;
                brt.anchoredPosition = (pa + pc) * 0.5f;
                brt.sizeDelta = new Vector2(d.magnitude, boneWidth);
                brt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            }
        }
    }

    static XRHandSubsystem GetSub()
    {
        var s = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(s);
        return s.Count > 0 ? s[0] : null;
    }
}
