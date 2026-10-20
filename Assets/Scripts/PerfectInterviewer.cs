using UnityEngine;
using System.Collections;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class PerfectInterviewer : MonoBehaviour
{
    private Animator animator;
    private Transform headBone;
    private Transform neckBone;
    private Transform spineBone;

    // --- 暴走防止用の初期値 ---
    private Quaternion initialSpineRotation;

    public enum NonverbalLevel
    {
        Low = 0,
        High = 1
    }

    [System.Serializable]
    public class NonverbalSettings
    {
        [Header("視線制御")]
        [Tooltip("相手を追って視線・頭を動かすか。Low条件では false にして正面固定にする。")]
        public bool gazeFollow = true;
        public float turnSpeed = 5.0f;
        public float maxHeadAngle = 80.0f;

        [Header("マイク・頷き")]
        [Range(0.001f, 0.1f)]
        public float volumeThreshold = 0.02f;
        [Tooltip("音量が閾値を下回っても「話している扱い」を保持する猶予（秒）。小さな途切れでタイマーがリセットされるのを防ぐ。")]
        public float voiceHangover = 0.25f;
        [Tooltip("音量の平滑化（指数移動平均）の追従速度。大きいほど素早く追従。")]
        public float volumeSmoothing = 10f;
        [Tooltip("うなずきを行うか。Low条件では false にして頷かせない（報告書の定義）。")]
        public bool nodEnabled = true;
        [Tooltip("うなずき間隔をこの範囲(秒)でランダムに選ぶ。報告書 High = 0.5〜2.0秒。")]
        public float nodIntervalMin = 0.5f;
        public float nodIntervalMax = 2.0f;
        public float nodAngle = 15f;
        [Tooltip("1回の頷きの所要時間（秒）。")]
        public float nodDuration = 0.45f;

        [Header("聞く姿勢（前傾）")]
        public float leanAngle = 15.0f;
        public float leanSpeed = 2.0f;
    }

    [Header("Non-verbal Behavior Presets (High / Low)")]
    public NonverbalSettings high = new NonverbalSettings();
    // 報告書 2026/06/18 の定義に忠実な Low：頷きなし・正面固定視線・無表情・静止姿勢
    public NonverbalSettings low = new NonverbalSettings
    {
        gazeFollow = false,      // 正面固定（相手を追わない）
        turnSpeed = 2.5f,        // gazeFollow=false のため実質未使用
        maxHeadAngle = 45.0f,    // 同上
        volumeThreshold = 0.03f,
        voiceHangover = 0.20f,
        volumeSmoothing = 8f,
        nodEnabled = false,      // 頷かない
        nodIntervalMin = 0.5f,
        nodIntervalMax = 2.0f,
        nodAngle = 6.0f,
        nodDuration = 0.55f,
        leanAngle = 0.0f,        // 静止姿勢（前傾しない）
        leanSpeed = 1.2f
    };

    [Header("Current Level")]
    public NonverbalLevel currentLevel = NonverbalLevel.High;

    // --- 設定：ターゲット（視線） ---
    [Header("視線制御")]
    public Transform target; // FaceTarget（赤い玉）
    public bool gazeFollow = true; // false で正面固定（Low条件）
    public float turnSpeed = 5.0f; // 追従スピード
    public float maxHeadAngle = 80.0f;

    // --- 視線スムージング用の仮想ターゲット ---
    private Vector3 currentLookPos; // 実際にアバターが見ている座標

    // --- 設定：マイクと頷き ---
    [Header("マイク・頷き")]
    [Range(0.001f, 0.1f)]
    public float volumeThreshold = 0.02f;
    public float voiceHangover = 0.25f;
    public float volumeSmoothing = 10f;
    public bool nodEnabled = true;      // false で頷かない（Low条件）
    public float nodIntervalMin = 0.5f; // うなずき間隔の下限（秒）
    public float nodIntervalMax = 2.0f; // うなずき間隔の上限（秒）
    public float nodAngle = 15f;
    public float nodDuration = 0.45f;

    // --- 設定：聞く姿勢 ---
    [Header("聞く姿勢（前傾）")]
    public float leanAngle = 15.0f;
    public float leanSpeed = 2.0f;

    [Header("Debug / Test")]
    public bool enableLeaning = true;
    [Tooltip("マイク入力なしで「相手が話している」状態を疑似的にONにする")]
    public bool simulateListening = false;

    [Header("マイク設定（VoiceInterviewClient と共有）")]
    [Tooltip("参加者マイクのデバイス名。空なら先頭デバイス。\n評価者用マイク（HumanVoiceRelay）と別デバイスになるよう明示指定する。\n使用可能デバイスは起動時に Console に出力")]
    public string micDevice = "";
    [Tooltip("マイクのリングバッファ長（秒）。参加者の1回の発話をこの長さまで録音できる")]
    public int micBufferSeconds = 60;
    [Tooltip("マイクのサンプリングレート (Hz)")]
    public int micFrequency = 44100;
    [Tooltip("ON の間はマイク入力を無視する。アバター自身の TTS 音声を拾って頷かないようにするため、\nVoiceInterviewClient が再生中に自動で ON にする")]
    public bool suppressListening = false;

    [Header("Runtime (read-only)")]
    [SerializeField] private float lastVolume = 0f;
    [SerializeField] private float smoothedVolume = 0f;
    [SerializeField] private float lastVoiceTime = -999f;

    // --- 内部変数 ---
    private AudioClip micClip;
    private string micName = null;
    private bool isTalking = false;
    private bool isNodding = false;
    private float currentLean = 0f;
    private float nodTimer = 0f;
    private float _currentNodInterval = 1.0f; // 次に頷くまでの間隔（毎回 min〜max でランダムに選ぶ）

    // 頷き計算用
    private float currentNodValue = 0f; // 今の頷き角度（0〜1〜0）

    void Start()
    {
        animator = GetComponent<Animator>();
        if (animator != null)
        {
            headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            neckBone = animator.GetBoneTransform(HumanBodyBones.Neck);
            spineBone = animator.GetBoneTransform(HumanBodyBones.Spine);

            if (spineBone != null) initialSpineRotation = spineBone.localRotation;
        }

        if (target == null && Camera.main != null) target = Camera.main.transform;

        // 初期視線位置
        if (target != null) currentLookPos = target.position;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => StartMic();
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
        }
        else
        {
            StartMic();
        }
#else
        StartMic();
#endif

        ApplyPreset(currentLevel);
    }

    void StartMic()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[PerfectInterviewer] マイクが見つかりません。");
            return;
        }
        Debug.Log("[PerfectInterviewer] 使用可能なマイク: " + string.Join(" / ", Microphone.devices));

        // 参加者マイクを明示指定（空なら先頭）。指定名が見つからなければ先頭にフォールバック
        micName = string.IsNullOrEmpty(micDevice) ? Microphone.devices[0] : micDevice;
        if (!System.Array.Exists(Microphone.devices, d => d == micName))
        {
            Debug.LogWarning($"[PerfectInterviewer] 指定マイク '{micDevice}' が見つからないため先頭を使用します。");
            micName = Microphone.devices[0];
        }
        micClip = Microphone.Start(micName, true, Mathf.Max(1, micBufferSeconds), micFrequency);
        Debug.Log($"[PerfectInterviewer] 参加者マイク: {micName}");
    }

    // ── VoiceInterviewClient がマイクを共有するためのアクセサ ──
    // マイクの起動はこのスクリプトに一本化し、録音側は同じ AudioClip を参照する
    public AudioClip GetMicClip() => micClip;
    public string GetMicName() => micName;
    public bool IsMicReady() => micClip != null && !string.IsNullOrEmpty(micName);

    /// <summary>
    /// マイクが止まっていたら起動し直す（他のスクリプトが同じデバイスで Microphone.End した場合など）。
    /// 止まっていた場合は true を返す。
    /// </summary>
    public bool EnsureMicRecording()
    {
        if (!string.IsNullOrEmpty(micName) && Microphone.IsRecording(micName)) return false;
        Debug.LogWarning($"[PerfectInterviewer] 参加者マイク '{micName}' が停止していたため再起動します。");
        StartMic();
        return true;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    IEnumerator WaitForMicPermission()
    {
        yield return new WaitUntil(() => Permission.HasUserAuthorizedPermission(Permission.Microphone));
        StartMic();
    }
#endif

    public void ApplyPreset(NonverbalLevel level)
    {
        currentLevel = level;
        var s = (level == NonverbalLevel.High) ? high : low;

        gazeFollow = s.gazeFollow;
        turnSpeed = s.turnSpeed;
        maxHeadAngle = s.maxHeadAngle;
        volumeThreshold = s.volumeThreshold;
        voiceHangover = Mathf.Max(0f, s.voiceHangover);
        volumeSmoothing = Mathf.Max(0.01f, s.volumeSmoothing);
        nodEnabled = s.nodEnabled;
        nodIntervalMin = Mathf.Max(0.05f, s.nodIntervalMin);
        nodIntervalMax = Mathf.Max(nodIntervalMin, s.nodIntervalMax);
        nodAngle = s.nodAngle;
        nodDuration = Mathf.Max(0.05f, s.nodDuration);
        leanAngle = s.leanAngle;
        leanSpeed = s.leanSpeed;

        nodTimer = 0f;
        _currentNodInterval = Random.Range(nodIntervalMin, nodIntervalMax);
        smoothedVolume = 0f;
        lastVoiceTime = -999f;
    }

    /// <summary>
    /// 報告書 2026/06/18 の定義に忠実な High / Low プリセットを流し込む。
    /// プレハブにシリアライズ済みの値を、コードの定義で上書きするために使う。
    /// インスペクタで PerfectInterviewer を右クリック → このメニューを実行。
    /// </summary>
    [ContextMenu("報告書プリセットを流し込む (High / Low)")]
    public void LoadReportPresets()
    {
        high = new NonverbalSettings
        {
            gazeFollow = true,
            turnSpeed = 5.0f,
            maxHeadAngle = 80.0f,
            volumeThreshold = 0.02f,
            voiceHangover = 0.25f,
            volumeSmoothing = 10f,
            nodEnabled = true,
            nodIntervalMin = 0.5f,   // 報告書 High = 0.5〜2.0秒
            nodIntervalMax = 2.0f,
            nodAngle = 15f,
            nodDuration = 0.45f,
            leanAngle = 15.0f,
            leanSpeed = 2.0f
        };
        low = new NonverbalSettings
        {
            gazeFollow = false,      // 正面固定
            turnSpeed = 2.5f,
            maxHeadAngle = 45.0f,
            volumeThreshold = 0.03f,
            voiceHangover = 0.20f,
            volumeSmoothing = 8f,
            nodEnabled = false,      // 頷かない
            nodIntervalMin = 0.5f,
            nodIntervalMax = 2.0f,
            nodAngle = 6.0f,
            nodDuration = 0.55f,
            leanAngle = 0.0f,        // 静止
            leanSpeed = 1.2f
        };
        ApplyPreset(currentLevel);
        Debug.Log("[PerfectInterviewer] 報告書プリセット (High/Low) を流し込みました。");
    }

    void Update()
    {
        // マイク処理
        // suppressListening 中（アバターが TTS で発話中）は自分の声を拾わないよう入力を無視する
        float raw = suppressListening ? 0f
                  : (simulateListening ? (volumeThreshold + 0.001f) : GetAveragedVolume());
        lastVolume = raw;

        // 音量を平滑化して閾値判定を安定させる
        float k = 1f - Mathf.Exp(-volumeSmoothing * Time.deltaTime);
        smoothedVolume = Mathf.Lerp(smoothedVolume, raw, k);

        bool voiceActive = !suppressListening && smoothedVolume > volumeThreshold;
        if (voiceActive)
            lastVoiceTime = Time.time;

        // 短い途切れは「話している扱い」で保持する
        bool talkingNow = !suppressListening
                       && (simulateListening || (Time.time - lastVoiceTime) <= voiceHangover);

        if (talkingNow)
        {
            isTalking = true;

            // 聞いている（相手が話している）間に頷く。
            // 間隔は 0.5〜2.0秒の範囲で毎回ランダムに選び、機械的な等間隔を避ける。
            // Low条件（nodEnabled=false）では一切頷かない。
            if (nodEnabled)
            {
                nodTimer += Time.deltaTime;
                if (!isNodding && nodTimer >= _currentNodInterval)
                {
                    nodTimer = 0f;
                    _currentNodInterval = Random.Range(nodIntervalMin, nodIntervalMax);
                    StartCoroutine(NodRoutine());
                }
            }
        }
        else
        {
            isTalking = false;
            // タイマーは保持（加算のみ停止）して、短い途切れで頷き間隔に到達できなくなるのを防ぐ
        }
    }

    public float GetLastVolume() => lastVolume;
    public float GetSmoothedVolume() => smoothedVolume;
    public bool GetIsTalking() => isTalking;
    public bool GetIsNodding() => isNodding;

    public void TriggerNod()
    {
        if (!isNodding)
        {
            nodTimer = 0f;
            StartCoroutine(NodRoutine());
        }
    }

    // ★重要：LateUpdateでAnimatorの結果を上書きする
    // ★修正版 LateUpdate：頷きを「首」から「頭」へ変更
    void LateUpdate()
    {
        if (animator == null) return;

        // ------------------------------------------------
        // 1. 体（Spine）の前傾
        // ------------------------------------------------
        if (spineBone != null)
        {
            float targetLean = (enableLeaning && isTalking) ? leanAngle : 0f;
            currentLean = Mathf.Lerp(currentLean, targetLean, Time.deltaTime * leanSpeed);
            spineBone.localRotation = initialSpineRotation * Quaternion.Euler(currentLean, 0, 0);
        }

        // ------------------------------------------------
        // 2. 首（Neck）はAnimatorにお任せ（何もしない）
        // ------------------------------------------------
        // ※ここを動かすと「きっしょく」なるので削除しました

        // ------------------------------------------------
        // 3. 頭（Head）で「視線」と「頷き」を両方やる
        // ------------------------------------------------
        // gazeFollow=false（Low条件）では相手を追わず、Animator の正面姿勢のまま固定する
        if (gazeFollow && headBone != null && target != null && headBone.parent != null)
        {
            // A. まずターゲットを見る方向を決める
            currentLookPos = Vector3.Lerp(currentLookPos, target.position, Time.deltaTime * turnSpeed);
            Vector3 direction = currentLookPos - headBone.position;

            // 角度制限
            Vector3 forward = transform.forward;
            float angle = Vector3.Angle(forward, direction);

            if (angle < maxHeadAngle)
            {
                // ベースの向き（ターゲットを見る：ワールド回転）
                Quaternion lookWorld = Quaternion.LookRotation(direction, Vector3.up);

                // 親基準のローカル回転に変換（前傾など親の回転と干渉しにくい）
                Quaternion lookLocal = Quaternion.Inverse(headBone.parent.rotation) * lookWorld;

                // B. そこに「頷き（Nod）」の角度を足し合わせる（ローカルX軸＝ピッチ）
                Quaternion nodLocal = Quaternion.Euler(currentNodValue, 0, 0);

                headBone.localRotation = lookLocal * nodLocal;
            }
        }
        else if (!gazeFollow && headBone != null && headBone.parent != null)
        {
            // Low条件：頭を体の正面に固定する。
            // ここで上書きしないと、待機アニメーション（Sitting Idle）に含まれる頭の揺れが
            // そのまま出て「うなずいている」ように見えてしまう。
            Quaternion forwardWorld = Quaternion.LookRotation(transform.forward, Vector3.up);
            headBone.localRotation = Quaternion.Inverse(headBone.parent.rotation) * forwardWorld;
        }
    }

    // 頷きの数値を計算するだけのコルーチン（直接回転はさせない）
    IEnumerator NodRoutine()
    {
        isNodding = true;
        float time = 0;
        float duration = nodDuration; // 頷きにかかる時間

        while (time < 1f)
        {
            time += Time.deltaTime / duration;
            // 0 -> 1 -> 0 の動きを作る（サイン波）
            // NodAngleの分だけ下を向く
            currentNodValue = Mathf.Sin(time * Mathf.PI) * nodAngle;
            yield return null;
        }

        currentNodValue = 0f;
        isNodding = false;
    }

    float GetAveragedVolume()
    {
        if (micClip == null) return 0;
        int sampleSize = 128;
        float[] data = new float[sampleSize];
        int micPosition = Microphone.GetPosition(micName) - (sampleSize + 1);
        if (micPosition < 0) return 0;
        micClip.GetData(data, micPosition);
        float total = 0;
        foreach (float s in data) total += Mathf.Abs(s);
        return total / sampleSize;
    }

    public void SetLeaningEnabled(bool enabled)
    {
        enableLeaning = enabled;
    }
}