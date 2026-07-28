using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VRM;

public class VrmExpressionApplier : MonoBehaviour
{
    [Header("VRM0.x: VRMBlendShapeProxy")]
    public VRMBlendShapeProxy proxy;

    [Header("Reset settings")]
    [Tooltip("口パク(A/I/U/E/O)をリセットしない（別制御したい場合ON）")]
    public bool keepLipSync = true;

    [Header("Test (OnGUI)")]
    public bool enableTestGUI = true;
    public bool enableKeyboardTest = true;

    [Header("Auto Demo")]
    [Tooltip("数秒ごとに表情を自動で切り替えるデモ")]
    public bool enableAutoDemo = false;
    [Range(0.2f, 10f)]
    public float autoDemoInterval = 2.0f;

    public enum NonverbalLevel
    {
        Low = 0,
        High = 1
    }

    [Header("Non-verbal Behavior (High / Low)")]
    public NonverbalLevel currentLevel = NonverbalLevel.High;
    [Range(0f, 2f)]
    public float expressionIntensityHigh = 1.0f;
    [Range(0f, 2f)]
    public float expressionIntensityLow = 0.55f;

    [Header("Preset Targets (optional)")]
    public PerfectInterviewer interviewer;
    public UdpReceiver udpReceiver;

    private float _expressionIntensity = 1.0f;

    // テスト用：ラベル一覧（あなたのプロンプトで出てくるもの中心）
    private readonly string[] _labels =
    {
        "笑顔", "穏やかな笑顔", "にこやか",
        "穏やか", "落ち着いた", "真顔",
        "うなずき", "感心", "好意的な笑顔",
        "真剣", "少し疑問", "確認するような表情",
        "困り顔", "苦笑い", "眉をひそめる",
        "厳しい", "真剣（強め）", "沈黙（数秒）",
        "優しい", "受容的", "安心させる笑顔",
        "丁寧な笑顔", "感謝", "会釈"
    };

    private int _selectedIndex = 0;
    private string _lastApplied = "";
    private Coroutine _demoCo;

    void Reset()
    {
        proxy = GetComponentInChildren<VRMBlendShapeProxy>();
    }

    void OnEnable()
    {
        ApplyPreset(currentLevel, applyToOthers: true);
        if (enableAutoDemo)
            StartAutoDemo();
    }

    void OnDisable()
    {
        StopAutoDemo();
    }

    void Update()
    {
#if UNITY_EDITOR

        if (!enableKeyboardTest) return;

        // キーで簡単テスト
        // 1: 笑顔  2: 真顔  3: 感心  4: 困り顔  5: 厳しい  6: 優しい  7: まばたき
        if (Input.GetKeyDown(KeyCode.Alpha1)) ApplyLabel("笑顔");
        if (Input.GetKeyDown(KeyCode.Alpha2)) ApplyLabel("真顔");
        if (Input.GetKeyDown(KeyCode.Alpha3)) ApplyLabel("感心");
        if (Input.GetKeyDown(KeyCode.Alpha4)) ApplyLabel("困り顔");
        if (Input.GetKeyDown(KeyCode.Alpha5)) ApplyLabel("厳しい");
        if (Input.GetKeyDown(KeyCode.Alpha6)) ApplyLabel("優しい");
        if (Input.GetKeyDown(KeyCode.Alpha7)) StartCoroutine(BlinkOnce());

        // R: 表情リセット
        if (Input.GetKeyDown(KeyCode.R)) ResetFacialOnly();
#endif
    }

    /// <summary>
    /// 文字ラベル（例：「穏やか」）を受けてVRM0.x表情に反映
    /// </summary>
    public void ApplyLabel(string label)
    {
        if (proxy == null)
        {
            Debug.LogWarning("[VrmExpressionApplier] proxy is null. Assign VRMBlendShapeProxy.");
            return;
        }

        ResetFacialOnly();

        switch (label)
        {
            // 親和
            case "笑顔":
            case "穏やかな笑顔":
            case "にこやか":
            case "丁寧な笑顔":
            case "安心させる笑顔":
                Set(BlendShapePreset.Fun, 0.80f);
                Set(BlendShapePreset.Joy, 0.30f);
                break;

            // 進行
            case "穏やか":
            case "落ち着いた":
            case "真顔":
                Set(BlendShapePreset.Neutral, 0.85f);
                break;

            // 評価
            case "うなずき":
                // 本来は頭ボーンで表現が自然。ここでは軽い好意表情だけ入れる
                Set(BlendShapePreset.Joy, 0.35f);
                Set(BlendShapePreset.Fun, 0.30f);
                break;
            case "感心":
                Set(BlendShapePreset.Joy, 0.55f);
                Set(BlendShapePreset.Fun, 0.20f);
                break;
            case "好意的な笑顔":
                Set(BlendShapePreset.Fun, 0.70f);
                Set(BlendShapePreset.Joy, 0.25f);
                break;

            // 検証
            case "真剣":
                Set(BlendShapePreset.Neutral, 0.90f);
                break;
            case "少し疑問":
            case "確認するような表情":
                // 標準プリセットだけだと難しいのでNeutral＋少しAngryで代用
                Set(BlendShapePreset.Neutral, 0.80f);
                Set(BlendShapePreset.Angry, 0.20f);
                break;

            // 懸念
            case "困り顔":
            case "眉をひそめる":
                Set(BlendShapePreset.Sorrow, 0.50f);
                Set(BlendShapePreset.Angry, 0.25f);
                break;
            case "苦笑い":
                Set(BlendShapePreset.Fun, 0.35f);
                Set(BlendShapePreset.Sorrow, 0.25f);
                break;

            // 警告
            case "厳しい":
            case "真剣（強め）":
                Set(BlendShapePreset.Angry, 0.60f);
                Set(BlendShapePreset.Neutral, 0.40f);
                break;
            case "沈黙（数秒）":
                Set(BlendShapePreset.Neutral, 0.90f);
                break;

            // 救済
            case "優しい":
            case "受容的":
            case "感謝":
            case "会釈":
                Set(BlendShapePreset.Fun, 0.55f);
                Set(BlendShapePreset.Neutral, 0.20f);
                break;

            default:
                // 未知はニュートラル
                Set(BlendShapePreset.Neutral, 0.70f);
                break;
        }

        proxy.Apply();
        _lastApplied = label;
        Debug.Log($"[VrmExpressionApplier] ApplyLabel: {label}");
    }

    private void Set(BlendShapePreset preset, float value)
    {
        float v = Mathf.Clamp01(value * _expressionIntensity);
        proxy.SetValue(preset, v);
    }

    public void ApplyPreset(NonverbalLevel level, bool applyToOthers)
    {
        currentLevel = level;
        _expressionIntensity = (level == NonverbalLevel.High) ? expressionIntensityHigh : expressionIntensityLow;

        if (!applyToOthers) return;

        if (interviewer != null)
        {
            interviewer.ApplyPreset(level == NonverbalLevel.High
                ? PerfectInterviewer.NonverbalLevel.High
                : PerfectInterviewer.NonverbalLevel.Low);
        }

        if (udpReceiver != null)
        {
            udpReceiver.ApplyPreset(level == NonverbalLevel.High);
        }
    }

    /// <summary>
    /// 表情系のみリセット（口パクを残すかは keepLipSync で制御）
    /// </summary>
    public void ResetFacialOnly()
    {
        if (proxy == null) return;

        // 表情系
        proxy.SetValue(BlendShapePreset.Angry, 0f);
        proxy.SetValue(BlendShapePreset.Sorrow, 0f);
        proxy.SetValue(BlendShapePreset.Fun, 0f);
        proxy.SetValue(BlendShapePreset.Joy, 0f);
        proxy.SetValue(BlendShapePreset.Neutral, 0f);
        proxy.SetValue(BlendShapePreset.Blink, 0f);
        proxy.SetValue(BlendShapePreset.Blink_L, 0f);
        proxy.SetValue(BlendShapePreset.Blink_R, 0f);

        // 口パク系も落としたい場合
        if (!keepLipSync)
        {
            proxy.SetValue(BlendShapePreset.A, 0f);
            proxy.SetValue(BlendShapePreset.I, 0f);
            proxy.SetValue(BlendShapePreset.U, 0f);
            proxy.SetValue(BlendShapePreset.E, 0f);
            proxy.SetValue(BlendShapePreset.O, 0f);
        }

        proxy.Apply();
    }

    /// <summary>
    /// テスト用：一回だけ瞬きを入れる
    /// </summary>
    private IEnumerator BlinkOnce()
    {
        if (proxy == null) yield break;

        // 表情は保持したまま瞬きだけ足す（Blinkのみ一時的に上げる）
        float t = 0f;
        float dur = 0.10f;
        while (t < dur)
        {
            t += Time.deltaTime;
            proxy.SetValue(BlendShapePreset.Blink, Mathf.Lerp(0f, 1f, t / dur));
            proxy.Apply();
            yield return null;
        }

        t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            proxy.SetValue(BlendShapePreset.Blink, Mathf.Lerp(1f, 0f, t / dur));
            proxy.Apply();
            yield return null;
        }

        proxy.SetValue(BlendShapePreset.Blink, 0f);
        proxy.Apply();
    }

    public void StartAutoDemo()
    {
        enableAutoDemo = true;
        if (_demoCo == null)
            _demoCo = StartCoroutine(AutoDemoCo());
    }

    public void StopAutoDemo()
    {
        enableAutoDemo = false;
        if (_demoCo != null)
        {
            StopCoroutine(_demoCo);
            _demoCo = null;
        }
    }

    private IEnumerator AutoDemoCo()
    {
        while (enableAutoDemo)
        {
            _selectedIndex = (_selectedIndex + 1) % _labels.Length;
            ApplyLabel(_labels[_selectedIndex]);
            yield return new WaitForSeconds(autoDemoInterval);
        }
        _demoCo = null;
    }
    // ===== Canvas UI 用公開ヘルパー =====
    public string GetLastApplied() => _lastApplied;
    public string GetSelectedLabel() => _labels[Mathf.Clamp(_selectedIndex, 0, _labels.Length - 1)];
    public void SelectPrevLabel() { _selectedIndex = (_selectedIndex - 1 + _labels.Length) % _labels.Length; }
    public void SelectNextLabel() { _selectedIndex = (_selectedIndex + 1) % _labels.Length; }
    public void ApplySelectedLabel() { ApplyLabel(_labels[Mathf.Clamp(_selectedIndex, 0, _labels.Length - 1)]); }
    public void TriggerBlinkOnce() { StartCoroutine(BlinkOnce()); }
    public void SetHighPreset() { ApplyPreset(NonverbalLevel.High, applyToOthers: true); }
    public void SetLowPreset()  { ApplyPreset(NonverbalLevel.Low,  applyToOthers: true); }
    public void TriggerNod()           { if (interviewer != null) interviewer.TriggerNod(); }
    public void SetLeaningEnabled(bool v)       { if (interviewer != null) interviewer.SetLeaningEnabled(v); }
    public void SetSimulateListening(bool v)    { if (interviewer != null) interviewer.simulateListening = v; }

#if UNITY_EDITOR
    void OnGUI()
    {
        if (!enableTestGUI) return;

        const int w = 420;
        const int h = 800;
        GUILayout.BeginArea(new Rect(10, 10, w, h), GUI.skin.box);

        GUILayout.Label("VrmExpressionApplier - Test GUI");
        GUILayout.Space(6);

        GUILayout.Label("Non-verbal Behavior (High / Low)");
        GUILayout.BeginHorizontal();
        bool toHigh = GUILayout.Toggle(currentLevel == NonverbalLevel.High, "High", GUILayout.Width(70));
        bool toLow = GUILayout.Toggle(currentLevel == NonverbalLevel.Low, "Low", GUILayout.Width(70));
        GUILayout.EndHorizontal();
        var next = currentLevel;
        if (toHigh && currentLevel != NonverbalLevel.High) next = NonverbalLevel.High;
        if (toLow && currentLevel != NonverbalLevel.Low) next = NonverbalLevel.Low;
        if (next != currentLevel)
            ApplyPreset(next, applyToOthers: true);

        if (interviewer != null)
        {
            GUILayout.Space(2);
            bool nextLeaning = GUILayout.Toggle(interviewer.enableLeaning, "Leaning (前傾) ON", GUILayout.Width(160));
            if (nextLeaning != interviewer.enableLeaning)
                interviewer.SetLeaningEnabled(nextLeaning);

            GUILayout.BeginHorizontal();
            bool nextSim = GUILayout.Toggle(interviewer.simulateListening, "Simulate Listening", GUILayout.Width(160));
            if (nextSim != interviewer.simulateListening)
                interviewer.simulateListening = nextSim;
            if (GUILayout.Button("Trigger Nod", GUILayout.Width(110)))
                interviewer.TriggerNod();
            GUILayout.EndHorizontal();

            GUILayout.Label($"Mic raw: {interviewer.GetLastVolume():0.000}  smooth: {interviewer.GetSmoothedVolume():0.000}  (th: {interviewer.volumeThreshold:0.000})  Talking: {interviewer.GetIsTalking()}  Nodding: {interviewer.GetIsNodding()}");
        }

        if (proxy == null)
        {
            GUI.color = Color.yellow;
            GUILayout.Label("proxy が未設定です。VRMBlendShapeProxyを割り当ててください。");
            GUI.color = Color.white;
        }
        else
        {
            GUILayout.Label($"proxy: {proxy.name}");
        }

        GUILayout.Space(6);
        GUILayout.Label($"Last Applied: {_lastApplied}");

        GUILayout.Space(10);
        GUILayout.Label("選択中の表情ラベル：");
        _selectedIndex = Mathf.Clamp(_selectedIndex, 0, _labels.Length - 1);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("<<", GUILayout.Width(50))) _selectedIndex = (_selectedIndex - 1 + _labels.Length) % _labels.Length;
        GUILayout.Label(_labels[_selectedIndex], GUILayout.Width(240));
        if (GUILayout.Button(">>", GUILayout.Width(50))) _selectedIndex = (_selectedIndex + 1) % _labels.Length;
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        if (GUILayout.Button("Apply Selected", GUILayout.Height(28)))
        {
            ApplyLabel(_labels[_selectedIndex]);
        }

        GUILayout.Space(6);
        if (GUILayout.Button("Blink Once (test)", GUILayout.Height(24)))
        {
            StartCoroutine(BlinkOnce());
        }

        GUILayout.Space(6);
        if (GUILayout.Button("Reset Facial Only (R key)", GUILayout.Height(24)))
        {
            ResetFacialOnly();
            _lastApplied = "RESET";
        }

        GUILayout.Space(10);
        GUILayout.Label("Keyboard:");
        GUILayout.Label("1:笑顔 2:真顔 3:感心 4:困り顔 5:厳しい 6:優しい 7:瞬き  R:リセット");

        GUILayout.Space(10);
        GUILayout.BeginHorizontal();
        enableAutoDemo = GUILayout.Toggle(enableAutoDemo, "Auto Demo");
        GUILayout.Label($"Interval: {autoDemoInterval:0.0}s");
        GUILayout.EndHorizontal();

        autoDemoInterval = GUILayout.HorizontalSlider(autoDemoInterval, 0.2f, 10f);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Start Demo"))
            StartAutoDemo();
        if (GUILayout.Button("Stop Demo"))
            StopAutoDemo();
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
#endif
}
