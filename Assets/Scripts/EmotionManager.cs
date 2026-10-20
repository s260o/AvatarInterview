using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using VRM;

public class VrmExpressionApplier : MonoBehaviour
{
    [Header("VRM0.x: VRMBlendShapeProxy")]
    public VRMBlendShapeProxy proxy;

    [Header("VRM 1.0 アバター（設定時はこちらを優先）")]
    [Tooltip("VRM 1.0 のアバターを使う場合は、アバターのルート（Vrm10Instance）を割り当てる")]
    public UniVRM10.Vrm10Instance vrm10;

    private AvatarFace _face;
    /// <summary>VRM 0.x / 1.0 を共通に扱う表情操作。</summary>
    private AvatarFace Face => _face ??= new AvatarFace(proxy, vrm10);

    // インスペクタで割り当てを変えたら作り直す
    void OnValidate()
    {
        _face = null;
        // プレイ中に強度を変えたら、表示中の表情にもすぐ反映する（調整用）
        _expressionIntensity = IntensityFor(currentLevel);
        _transitioning = true;
    }

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
    [Tooltip("High条件の表情の強さ。1 より大きくすると表情が大げさになる（各表情の重みは最大 1.0＝モデルの最大の表情で頭打ち）")]
    [Range(0f, 2f)]
    public float expressionIntensityHigh = 1.0f;
    // Low条件は報告書の定義どおり常に無表情。設定値で変わらないようコードで固定する。

    [Header("High条件の人間らしさ")]
    [Tooltip("反応の表情を保つ時間（秒）の範囲。過ぎたら落ち着いた表情へ戻る（人は同じ表情を長く保たない）")]
    public float reactionHoldMin = 2.5f;
    public float reactionHoldMax = 5.0f;
    [Tooltip("落ち着いたときの表情（relaxed）の強さ。0 なら素の顔")]
    [Range(0f, 0.5f)]
    public float restingRelaxed = 0.15f;
    [Tooltip("同じラベルでも毎回少しだけ強さを揺らす割合（毎回まったく同じ顔になるのを避ける）")]
    [Range(0f, 0.4f)]
    public float weightJitter = 0.15f;

    [Header("まばたき（自動）")]
    [Tooltip("生理的な動作として両条件で同じように入れるのが既定（非言語条件の操作には含めない）")]
    public bool blinkInHigh = true;
    public bool blinkInLow = true;
    [Tooltip("まばたきの間隔（秒）の範囲。人は平均 3〜4 秒に1回程度")]
    public float blinkIntervalMin = 2.0f;
    public float blinkIntervalMax = 6.0f;
    [Tooltip("2回続けてまばたきする確率")]
    [Range(0f, 0.5f)]
    public float doubleBlinkChance = 0.12f;
    [Tooltip("閉じる / 閉じたまま / 開く にかかる時間（秒）")]
    public float blinkCloseSeconds = 0.06f;
    public float blinkHoldSeconds = 0.04f;
    public float blinkOpenSeconds = 0.12f;

    [Header("表情の切り替え")]
    [Tooltip("表情が切り替わるのにかかる時間（秒）。0 で即時に切り替わる")]
    [Range(0f, 2f)]
    public float transitionSeconds = 0.4f;

    [Header("Preset Targets (optional)")]
    public PerfectInterviewer interviewer;
    public UdpReceiver udpReceiver;

    private float _expressionIntensity = 1.0f;

    // 感情の表情をなめらかに変化させるための状態（まばたき・口は対象外で即時反映）
    private static readonly FaceKey[] EmotionKeys =
        { FaceKey.Neutral, FaceKey.Joy, FaceKey.Fun, FaceKey.Angry, FaceKey.Sorrow, FaceKey.Surprised };
    private readonly float[] _baseWeights = new float[EmotionKeys.Length];    // ラベルが指定した重み（強度をかける前）
    private readonly float[] _currentWeights = new float[EmotionKeys.Length]; // いま表示している重み
    private bool _transitioning = false;

    // High条件: 反応の表情 → 一定時間後に落ち着いた表情へ戻す
    private float _reactionEnd = 0f;
    private bool _resting = true;

    // 自動まばたき
    private float _nextBlinkTime = 0f;
    private bool _blinking = false;

    private float IntensityFor(NonverbalLevel level) => level == NonverbalLevel.High ? expressionIntensityHigh : 0f;

    // 表情の方針の版（Server/main.py の EXPRESSION_POLICY_VERSION と一致させる）
    //   supportive_v1: High を「支持的で、発話に適切に反応する傾聴行動」と定義。
    //   評価・賞賛・批判に見える表情は実験中に出さない。
    public const string ExpressionPolicy = "supportive_v1";

    // 実験中に出してよい表情。Server/main.py の EXPRESSION_LABELS と完全に一致させること
    private static readonly string[] _labels =
    {
        // 基本の傾聴
        "穏やか", "落ち着いた", "穏やかな笑顔",
        // 関心・共感
        "興味津々", "優しい", "受容的", "真剣",
        // あいさつ
        "丁寧な笑顔", "会釈",
    };
    // 一覧にないラベル（旧方針のラベルなど）が来たときに使う表情
    public const string DefaultLabel = "穏やか";

    public static bool IsAllowedLabel(string label) => System.Array.IndexOf(_labels, label) >= 0;

    // ── 直近の表情の記録（遠隔操作の状態表示・ログ用）──
    /// <summary>直近に要求されたラベル。</summary>
    public string LastRequestedLabel { get; private set; } = "";
    /// <summary>直近に実際に適用したラベル（Low で抑制した場合は空）。</summary>
    public string LastAppliedLabel { get; private set; } = "";
    /// <summary>直近の要求が、許可一覧にないため置き換えられたか。</summary>
    public bool LastReplaced { get; private set; }
    /// <summary>直近の要求が、Low 条件のため無表情にされたか。</summary>
    public bool LastSuppressedByLow { get; private set; }
    /// <summary>直近の要求元（ai / manual / editor）。</summary>
    public string LastSource { get; private set; } = "";
    /// <summary>表情を適用するたびに 1 増える番号（変化の検出用）。</summary>
    public int ExpressionEventId { get; private set; }
    /// <summary>反応の表情を保ち終え、落ち着いた顔（Low は無表情）に戻っているか。</summary>
    public bool IsResting => _resting;

    private int _selectedIndex = 0;
    private Coroutine _demoCo;

    void Reset()
    {
        proxy = GetComponentInChildren<VRMBlendShapeProxy>();
        vrm10 = GetComponentInChildren<UniVRM10.Vrm10Instance>();
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
        // 無効化でまばたきのコルーチンが止まるため、状態を戻しておく（再有効化後も続けてまばたきできるように）
        _blinking = false;
        _nextBlinkTime = 0f;
    }

    void Update()
    {
        // High条件: 反応の表情を一定時間保ったら、落ち着いた表情へゆっくり戻す
        if (currentLevel == NonverbalLevel.High && !_resting && Time.time >= _reactionEnd)
            SetResting();

        UpdateExpressionTransition();
        UpdateAutoBlink();

#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM

        if (!enableKeyboardTest) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        // キーで簡単テスト（実験で使う表情だけを割り当てる）
        // 1: 穏やかな笑顔  2: 穏やか  3: 興味津々  4: 真剣  5: 優しい  6: 受容的  7: まばたき
        if (kb.digit1Key.wasPressedThisFrame) ApplyLabel("穏やかな笑顔");
        if (kb.digit2Key.wasPressedThisFrame) ApplyLabel("穏やか");
        if (kb.digit3Key.wasPressedThisFrame) ApplyLabel("興味津々");
        if (kb.digit4Key.wasPressedThisFrame) ApplyLabel("真剣");
        if (kb.digit5Key.wasPressedThisFrame) ApplyLabel("優しい");
        if (kb.digit6Key.wasPressedThisFrame) ApplyLabel("受容的");
        if (kb.digit7Key.wasPressedThisFrame) StartCoroutine(BlinkOnce());

        // R: 表情リセット
        if (kb.rKey.wasPressedThisFrame) ResetFacialOnly();
#endif
    }

    /// <summary>
    /// 文字ラベル（例：「穏やかな笑顔」）を受けて表情に反映し、実際に適用したラベルを返す。
    ///   ・許可一覧（supportive_v1）にないラベルは、既定の「穏やか」に置き換える
    ///     （AI の出力・手動操作・テスト操作のどの経路から来ても同じ）
    ///   ・Low : 常に無表情（ラベルは記録だけして表情は出さない。戻り値は空）
    ///   ・High: ラベルの表情を少し揺らして出し、数秒後に落ち着いた表情へ戻す
    /// </summary>
    /// <param name="source">要求元。ai / manual / editor（ログで区別するため）</param>
    public string ApplyLabel(string label, string source = "editor")
    {
        if (!Face.IsValid)
        {
            Debug.LogWarning("[VrmExpressionApplier] 表情を操作するアバターが未設定です（VRM 0.x は proxy、VRM 1.0 は vrm10 を割り当てる）。");
            return "";
        }

        string requested = label ?? "";
        bool replaced = !IsAllowedLabel(requested);
        string resolved = replaced ? DefaultLabel : requested;
        if (replaced)
            Debug.LogWarning($"[VrmExpressionApplier] 許可されていない表情「{requested}」を「{resolved}」に置き換えました（方針 {ExpressionPolicy}）。");

        LastRequestedLabel = requested;
        LastReplaced = replaced;
        LastSource = source;
        ExpressionEventId++;

        // 次の表情の目標をいったん全部 0 にし、下の switch で必要なものだけ設定する
        System.Array.Clear(_baseWeights, 0, _baseWeights.Length);

        if (currentLevel == NonverbalLevel.Low)
        {
            // Low条件は報告書の定義どおり無表情。ラベルが来ても表情は出さない
            LastAppliedLabel = "";
            LastSuppressedByLow = true;
            _resting = true;
            _transitioning = true;
            Debug.Log($"[VrmExpressionApplier] ApplyLabel: {requested} → Low のため無表情（{source}）");
            return "";
        }

        // 重みの対応（VRM 1.0）: Joy=happy, Fun=relaxed, Sorrow=sad, Surprised=surprised, Neutral=neutral
        // supportive_v1 では Angry を使わない。Sorrow・Surprised も、悲しみや大げさな驚きに見えない程度に抑える。
        switch (resolved)
        {
            // ── 基本の傾聴 ──
            case "穏やか":
                Set(FaceKey.Fun, 0.30f);
                break;
            case "落ち着いた":
                Set(FaceKey.Neutral, 0.40f);
                Set(FaceKey.Fun, 0.10f);
                break;
            case "穏やかな笑顔":
                Set(FaceKey.Fun, 0.70f);
                Set(FaceKey.Joy, 0.20f);
                break;

            // ── 関心・共感 ──
            case "興味津々":
                Set(FaceKey.Fun, 0.50f);
                Set(FaceKey.Surprised, 0.15f);
                break;
            case "優しい":
                Set(FaceKey.Fun, 0.55f);
                break;
            case "受容的":
                Set(FaceKey.Fun, 0.45f);
                Set(FaceKey.Neutral, 0.20f);
                break;
            case "真剣":
                // つらい話・失敗の話を、笑顔にせず真剣に聴く
                Set(FaceKey.Neutral, 0.70f);
                break;

            // ── あいさつ ──
            case "丁寧な笑顔":
                Set(FaceKey.Fun, 0.75f);
                Set(FaceKey.Joy, 0.25f);
                break;
            case "会釈":
                Set(FaceKey.Fun, 0.55f);
                Set(FaceKey.Joy, 0.20f);
                break;
        }

        // 同じラベルでも毎回まったく同じ顔にならないよう、強さを少しだけ揺らす
        for (int i = 0; i < _baseWeights.Length; i++)
            if (_baseWeights[i] > 0f)
                _baseWeights[i] = Mathf.Clamp01(_baseWeights[i] * Random.Range(1f - weightJitter, 1f + weightJitter));

        // 反応の表情はしばらく保ち、その後 Update で落ち着いた表情へ戻す
        _reactionEnd = Time.time + Random.Range(reactionHoldMin, Mathf.Max(reactionHoldMin, reactionHoldMax));
        _resting = false;

        // 実際の反映は UpdateExpressionTransition で少しずつ行う
        _transitioning = true;
        if (transitionSeconds <= 0f) UpdateExpressionTransition(snap: true);
        LastAppliedLabel = resolved;
        LastSuppressedByLow = false;
        Debug.Log($"[VrmExpressionApplier] ApplyLabel: {requested}{(replaced ? $" → {resolved}" : "")}（{source}）");
        return resolved;
    }

    /// <summary>落ち着いた表情（ほぼ素の顔に、ごく軽い relaxed）へ戻す。</summary>
    private void SetResting()
    {
        System.Array.Clear(_baseWeights, 0, _baseWeights.Length);
        if (currentLevel == NonverbalLevel.High && restingRelaxed > 0f)
            _baseWeights[System.Array.IndexOf(EmotionKeys, FaceKey.Fun)] = restingRelaxed;
        _resting = true;
        _transitioning = true;
    }

    private void Set(FaceKey preset, float value)
    {
        int i = System.Array.IndexOf(EmotionKeys, preset);
        if (i >= 0)
        {
            _baseWeights[i] = value; // 感情の表情は目標値として記録し、なめらかに近づける
            return;
        }
        // 感情以外（まばたき・口）は即時に反映する
        Face.Set(preset, Mathf.Clamp01(value * _expressionIntensity));
    }

    /// <summary>
    /// 表示中の表情の重みを、目標値に向けて少しずつ近づける（毎フレーム呼ぶ）。
    /// transitionSeconds でほぼ（約99%）目標に届く指数カーブで、動き出しが速く最後はゆっくり落ち着く。
    /// </summary>
    private void UpdateExpressionTransition(bool snap = false)
    {
        if (!_transitioning || !Face.IsValid) return;

        float k = (snap || transitionSeconds <= 0f)
            ? 1f
            : 1f - Mathf.Exp(-4.6f * Time.deltaTime / transitionSeconds);

        bool done = true;
        for (int i = 0; i < EmotionKeys.Length; i++)
        {
            float target = Mathf.Clamp01(_baseWeights[i] * _expressionIntensity);
            float next = Mathf.Lerp(_currentWeights[i], target, k);
            if (Mathf.Abs(next - target) < 0.002f) next = target;
            else done = false;

            _currentWeights[i] = next;
            Face.Set(EmotionKeys[i], next);
        }
        Face.Apply();

        if (done) _transitioning = false;
    }

    public void ApplyPreset(NonverbalLevel level, bool applyToOthers)
    {
        currentLevel = level;
        _expressionIntensity = IntensityFor(level);
        // 条件が切り替わったら、表示中の表情をいったん落ち着いた状態（Low は無表情）へなめらかに戻す
        SetResting();

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
        if (!Face.IsValid) return;

        System.Array.Clear(_baseWeights, 0, _baseWeights.Length);
        System.Array.Clear(_currentWeights, 0, _currentWeights.Length);
        _transitioning = false;

        // 表情系
        Face.Set(FaceKey.Angry, 0f);
        Face.Set(FaceKey.Sorrow, 0f);
        Face.Set(FaceKey.Fun, 0f);
        Face.Set(FaceKey.Joy, 0f);
        Face.Set(FaceKey.Neutral, 0f);
        Face.Set(FaceKey.Surprised, 0f);
        Face.Set(FaceKey.Blink, 0f);
        Face.Set(FaceKey.Blink_L, 0f);
        Face.Set(FaceKey.Blink_R, 0f);

        // 口パク系も落としたい場合
        if (!keepLipSync)
        {
            Face.Set(FaceKey.A, 0f);
            Face.Set(FaceKey.I, 0f);
            Face.Set(FaceKey.U, 0f);
            Face.Set(FaceKey.E, 0f);
            Face.Set(FaceKey.O, 0f);
        }

        Face.Apply();
    }

    /// <summary>ランダムな間隔で自動的にまばたきする（毎フレーム呼ぶ）。</summary>
    private void UpdateAutoBlink()
    {
        bool enabledNow = currentLevel == NonverbalLevel.High ? blinkInHigh : blinkInLow;
        if (!enabledNow || _blinking || !Face.IsValid) return;

        if (_nextBlinkTime <= 0f)
        {
            _nextBlinkTime = Time.time + Random.Range(blinkIntervalMin, blinkIntervalMax);
            return;
        }
        if (Time.time < _nextBlinkTime) return;

        StartCoroutine(AutoBlinkCo());
    }

    private IEnumerator AutoBlinkCo()
    {
        _blinking = true;
        int count = Random.value < doubleBlinkChance ? 2 : 1;
        for (int n = 0; n < count; n++)
        {
            yield return BlinkCurve(0f, 1f, blinkCloseSeconds);
            if (blinkHoldSeconds > 0f) yield return new WaitForSeconds(blinkHoldSeconds);
            yield return BlinkCurve(1f, 0f, blinkOpenSeconds);
            if (n + 1 < count) yield return new WaitForSeconds(0.08f);
        }
        Face.Set(FaceKey.Blink, 0f);
        Face.Apply();
        _nextBlinkTime = Time.time + Random.Range(blinkIntervalMin, Mathf.Max(blinkIntervalMin, blinkIntervalMax));
        _blinking = false;
    }

    private IEnumerator BlinkCurve(float from, float to, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            Face.Set(FaceKey.Blink, Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds)));
            Face.Apply();
            yield return null;
        }
        Face.Set(FaceKey.Blink, to);
        Face.Apply();
    }

    /// <summary>
    /// テスト用：一回だけ瞬きを入れる
    /// </summary>
    private IEnumerator BlinkOnce()
    {
        if (!Face.IsValid) yield break;

        // 表情は保持したまま瞬きだけ足す（Blinkのみ一時的に上げる）
        float t = 0f;
        float dur = 0.10f;
        while (t < dur)
        {
            t += Time.deltaTime;
            Face.Set(FaceKey.Blink, Mathf.Lerp(0f, 1f, t / dur));
            Face.Apply();
            yield return null;
        }

        t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            Face.Set(FaceKey.Blink, Mathf.Lerp(1f, 0f, t / dur));
            Face.Apply();
            yield return null;
        }

        Face.Set(FaceKey.Blink, 0f);
        Face.Apply();
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
    public string GetLastApplied() => LastSuppressedByLow ? $"{LastRequestedLabel}（Low: 無表情）" : LastAppliedLabel;
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

        if (!Face.IsValid)
        {
            GUI.color = Color.yellow;
            GUILayout.Label("表情の操作先が未設定です。VRM 0.x は proxy、VRM 1.0 は vrm10 を割り当ててください。");
            GUI.color = Color.white;
        }
        else
        {
            GUILayout.Label($"表情の操作先: {Face.Describe}");
        }

        GUILayout.Space(6);
        GUILayout.Label($"Last Applied: {GetLastApplied()}  (requested: {LastRequestedLabel}, {ExpressionPolicy})");

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
        }

        GUILayout.Space(10);
        GUILayout.Label("Keyboard:");
        GUILayout.Label("1:穏やかな笑顔 2:穏やか 3:興味津々 4:真剣 5:優しい 6:受容的 7:瞬き  R:リセット");

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
