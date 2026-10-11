// EgoBootstrap.cs
// 运行时自动搭好最小可用场景:相机源 + 头锁定的世界空间预览面板。
// 不需要在编辑器里手动拖任何东西,直接 build 即可。
// 之后做正式 UI 时可以删掉它、改用场景里手摆的 Canvas。

using UnityEngine;
using UnityEngine.UI;

public static class EgoBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        Debug.Log("[EgoBootstrap] init begin");

        // 1) 相机源
        var rig = new GameObject("EgoRig");
        Object.DontDestroyOnLoad(rig);
        var src = rig.AddComponent<EgoCameraSource>();

        // 录制器(订阅相机帧 → mp4),预览照常 = 录制时预览不消失
        var rec = rig.AddComponent<EgoRecorder>();
        rec.source = src;

        // 2) 世界空间 Canvas（HUD）
        var canvasGo = new GameObject("EgoPreviewCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(640, 480);
        canvasGo.transform.localScale = Vector3.one * 0.0008f; // 小 HUD 面板(≈0.51m@1m):采数据不挡视线,面板边缘即录制边界

        // 3) RawImage（铺满 Canvas = 录制区域）
        var imgGo = new GameObject("Preview");
        imgGo.transform.SetParent(canvasGo.transform, false);
        var raw = imgGo.AddComponent<RawImage>();
        raw.color = Color.white;
        var irt = raw.rectTransform;
        irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
        irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
        src.previewImage = raw;

        // 3b) 录制红点提示(录制时显示在预览顶部中央)
        var recGo = new GameObject("RecDot");
        recGo.transform.SetParent(canvasGo.transform, false);
        var recImg = recGo.AddComponent<RawImage>();
        recImg.color = Color.red;
        var rdrt = recImg.rectTransform;
        rdrt.anchorMin = new Vector2(0.5f, 1f); rdrt.anchorMax = new Vector2(0.5f, 1f);
        rdrt.pivot = new Vector2(0.5f, 1f);
        rdrt.anchoredPosition = new Vector2(0f, -16f);
        rdrt.sizeDelta = new Vector2(48f, 48f);
        recGo.SetActive(false);
        rec.recIndicator = recGo;
        rec.autoTest = false; // 改用音量键控制录制

        // 3c) 状态文字(底部):提示 + 录制计时 + 结束反馈
        // 字体保护:取不到内置字体就跳过(否则 null 字体的 Text 会让整个 Canvas 渲染崩掉)
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null)
        {
            var statusGo = new GameObject("Status");
            statusGo.transform.SetParent(canvasGo.transform, false);
            var status = statusGo.AddComponent<Text>();
            status.font = font;
            status.fontSize = 26;
            status.alignment = TextAnchor.LowerCenter;
            status.horizontalOverflow = HorizontalWrapMode.Overflow;
            status.color = Color.white;
            status.text = "Vol key: START recording";   // 英文,内置字体无中文字形
            var srt = status.rectTransform;
            srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(1f, 0f);
            srt.pivot = new Vector2(0.5f, 0f);
            srt.offsetMin = new Vector2(0f, 6f); srt.offsetMax = new Vector2(0f, 90f);
            rec.statusText = status;
            Debug.Log("[EgoBootstrap] status text created");
        }
        else { Debug.LogWarning("[EgoBootstrap] builtin font null; status text skipped"); }

        // 4) 放到头部正前方（头锁定）
        var cam = Camera.main;
        if (cam != null)
        {
            canvasGo.transform.SetParent(cam.transform, false);
            canvasGo.transform.localPosition = new Vector3(0f, -0.05f, 1.0f);
            canvasGo.transform.localRotation = Quaternion.identity;
            Debug.Log("[EgoBootstrap] canvas attached to Camera.main");
        }
        else
        {
            canvasGo.transform.position = new Vector3(0f, 1.3f, 1.0f);
            Debug.LogWarning("[EgoBootstrap] Camera.main is null; placed canvas at world pos");
        }

        // 不显示透视背景(VR 舒适):相机背景设为不透明,挡住 seethrough。
        // 注意:seethrough 本身要保持开启(相机取流必需,EgoCameraSource 会开),这里只挡显示。
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 1f); // 不透明
        }

        // 手部关节可视化:投影到预览面板上(叠在摄像头画面上,像 EgoKit)
        var viz = rig.AddComponent<HandJointViz>();
        viz.head = cam != null ? cam.transform : null;
        viz.previewCanvas = canvasGo.GetComponent<RectTransform>();
        viz.previewImage = raw;
        viz.cameraSource = src;

        Debug.Log("[EgoBootstrap] init done");
    }
}
