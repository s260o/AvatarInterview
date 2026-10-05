using System.Collections;
using UnityEngine;

/// <summary>
/// 案X（Link / PC 運用）用の人間音声リレー。
/// 評価者のマイク（PC の入力デバイス）を取得し、アバターの VoiceOutput(AudioSource) から
/// 準リアルタイムで再生する。AvatarLipSync が VoiceOutput を監視しているので口も動く。
///
/// 前提（案X）：
///  - 参加者→評価者：評価者が同室で参加者の実声を直接聞く（デジタル転送は不要）。
///    ※参加者はヘッドセット音声を聞くため、評価者の生声が直接混ざらないよう配慮する。
///  - 評価者→参加者：本スクリプトが評価者マイクを VoiceOutput から再生（＝アバターの声）。
///  - 評価者マイクは PerfectInterviewer が使う参加者マイクとは「別デバイス」にすること。
/// </summary>
[DisallowMultipleComponent]
public class HumanVoiceRelay : MonoBehaviour
{
    [Header("参照")]
    [Tooltip("再生先。AvatarLipSync が監視しているのと同じ VoiceOutput を指定する")]
    public AudioSource voiceOutput;

    [Header("マイク")]
    [Tooltip("評価者マイクのデバイス名。空なら既定デバイス。使用可能なデバイスは起動時に Console に出力される")]
    public string micDevice = "";
    [Tooltip("マイクのサンプリングレート(Hz)")]
    public int sampleRate = 16000;
    [Tooltip("再生の遅延バッファ（秒）。小さいほど低遅延だが途切れやすい")]
    [Range(0.05f, 0.5f)]
    public float latencySeconds = 0.15f;

    [Header("マイク共有（マイクが1本しかない場合の検証用）")]
    [Tooltip("ON にすると自前で Microphone.Start せず、PerfectInterviewer が既に開いているマイクを共有する。\n二重 Start による録音失敗を避けられる。※本番は評価者専用マイクを別デバイスで使うため OFF")]
    public bool useSharedMic = false;
    [Tooltip("共有元。未設定なら自動取得")]
    public PerfectInterviewer shareFrom;

    [Header("デバッグ")]
    [Tooltip("Play 時に自動で中継を開始する（条件を起動せず単体テストしたいとき）")]
    public bool debugStartOnPlay = false;

    [Header("状態（読み取り専用）")]
    [SerializeField] private bool relaying = false;

    private AudioClip _clip;
    private string _device;
    private bool _ownsMic = false; // 自前で Start したマイクか（共有なら false）

    public bool IsRelaying => relaying;

    void Awake()
    {
        Debug.Log("[HumanVoiceRelay] 使用可能なマイク: " +
            (Microphone.devices.Length == 0 ? "(なし)" : string.Join(" / ", Microphone.devices)));
    }

    void Start()
    {
        if (shareFrom == null) shareFrom = FindObjectOfType<PerfectInterviewer>();
        if (debugStartOnPlay) StartCoroutine(DebugAutoStart());
    }

    private IEnumerator DebugAutoStart()
    {
        // アバターは SeatInterviewerAtTable により配置完了まで一時的に非アクティブになる。
        // VoiceOutput（アバター上）が有効になり、共有マイクが準備できるまで待ってから開始する。
        float t = 0f;
        while (t < 15f)
        {
            bool voReady = (voiceOutput != null && voiceOutput.isActiveAndEnabled);
            bool micReady = !useSharedMic || (shareFrom != null && shareFrom.IsMicReady());
            if (voReady && micReady) break;
            t += Time.deltaTime;
            yield return null;
        }
        StartRelay();
    }

    /// <summary>評価者マイク → VoiceOutput の中継を開始する（人間条件で呼ぶ）。</summary>
    [ContextMenu("中継を開始 (StartRelay)")]
    public void StartRelay()
    {
        if (relaying) return;
        if (voiceOutput == null)
        {
            Debug.LogError("[HumanVoiceRelay] voiceOutput 未設定。中継できません。");
            return;
        }

        if (useSharedMic)
        {
            // マイクが1本しかない場合：PerfectInterviewer が既に開いているマイクを共有する
            if (shareFrom == null) shareFrom = FindObjectOfType<PerfectInterviewer>();
            if (shareFrom == null || !shareFrom.IsMicReady())
            {
                Debug.LogError("[HumanVoiceRelay] 共有元（PerfectInterviewer）のマイクが未準備です。");
                return;
            }
            _clip = shareFrom.GetMicClip();
            _device = shareFrom.GetMicName();
            _ownsMic = false;
        }
        else
        {
            if (Microphone.devices.Length == 0)
            {
                Debug.LogError("[HumanVoiceRelay] マイクが見つかりません。");
                return;
            }
            _device = string.IsNullOrEmpty(micDevice) ? Microphone.devices[0] : micDevice;
            _clip = Microphone.Start(_device, true, 1, sampleRate); // 1秒ループバッファ
            _ownsMic = true;
            if (_clip == null)
            {
                Debug.LogError($"[HumanVoiceRelay] マイク開始に失敗: {_device}");
                return;
            }
        }

        relaying = true;
        StartCoroutine(BeginPlayback());
        Debug.Log($"[HumanVoiceRelay] 中継開始（device={_device} / 共有={useSharedMic}）");
    }

    private IEnumerator BeginPlayback()
    {
        // マイクが録音を開始するまで待つ
        while (relaying && Microphone.GetPosition(_device) <= 0) yield return null;
        if (!relaying) yield break;

        voiceOutput.clip = _clip;
        voiceOutput.loop = true;
        voiceOutput.timeSamples = 0;
        voiceOutput.Play();
    }

    void Update()
    {
        if (!relaying || _clip == null || voiceOutput == null || !voiceOutput.isPlaying) return;

        // 再生位置がマイク書き込み位置から latency 分だけ後ろを追う状態に保つ（ドリフト補正）
        int clipLen = _clip.samples;
        if (clipLen <= 0) return;
        int micPos = Microphone.GetPosition(_device);
        if (micPos < 0) return;

        int target = Mathf.RoundToInt(latencySeconds * _clip.frequency);
        int playPos = voiceOutput.timeSamples;
        int trail = (((micPos - playPos) % clipLen) + clipLen) % clipLen; // 0..clipLen

        // ドリフトが 200ms 以上ずれたら再同期
        if (Mathf.Abs(trail - target) > _clip.frequency / 5)
        {
            int newPos = (((micPos - target) % clipLen) + clipLen) % clipLen;
            voiceOutput.timeSamples = Mathf.Clamp(newPos, 0, clipLen - 1);
        }
    }

    /// <summary>中継を停止する（AI条件・セッション終了時に呼ぶ）。</summary>
    [ContextMenu("中継を停止 (StopRelay)")]
    public void StopRelay()
    {
        if (!relaying) return;
        relaying = false;

        if (voiceOutput != null)
        {
            if (voiceOutput.isPlaying) voiceOutput.Stop();
            voiceOutput.loop = false;
            voiceOutput.clip = null; // TTS 再生に戻せるようクリア
        }
        // 自前で開いたマイクだけ閉じる（共有マイクは PerfectInterviewer が管理）
        if (_ownsMic && !string.IsNullOrEmpty(_device) && Microphone.IsRecording(_device))
            Microphone.End(_device);

        // 誰が停止を呼んだかを特定するためスタックトレースを出す
        Debug.Log("[HumanVoiceRelay] 中継停止\n呼び出し元:\n" + StackTraceUtility.ExtractStackTrace());
    }

    void OnDisable()
    {
        Debug.Log("[HumanVoiceRelay] OnDisable（GameObject/コンポーネントが無効化されました）");
        StopRelay();
    }
}
