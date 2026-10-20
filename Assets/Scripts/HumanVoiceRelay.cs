using System;
using System.Collections;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 人間条件（WoZ）で、評価者の声をアバターの VoiceOutput(AudioSource) から再生する。
/// AvatarLipSync が VoiceOutput を監視しているので口も動く。
///
/// 音声の取り込み元は2通り:
///  - Network（既定）: 実験者ページ（http://サーバー:8001/experimenter）で評価者マイクを ON にすると、
///    ブラウザが 16kHz/16bit/mono の PCM をサーバー経由で送ってくる。Quest 単体運用向け。
///  - LocalMic: Unity を動かしている PC のマイクを直接使う（Link / PC 運用向け）。
///
/// 評価者の声を流している間は PerfectInterviewer.suppressListening を ON にし、
/// スピーカーから出た評価者の声を参加者の発話と誤認して頷かないようにする。
/// </summary>
[DisallowMultipleComponent]
public class HumanVoiceRelay : MonoBehaviour
{
    public enum RelaySource { Network, LocalMic }

    [Header("参照")]
    [Tooltip("再生先。AvatarLipSync が監視しているのと同じ VoiceOutput を指定する")]
    public AudioSource voiceOutput;
    [Tooltip("サーバー URL の取得元（Network）")]
    public VoiceInterviewClient voiceClient;
    [Tooltip("参加者マイク（suppressListening の制御・マイク共有元）")]
    public PerfectInterviewer shareFrom;

    [Header("音声の取り込み元")]
    public RelaySource source = RelaySource.Network;

    [Header("Network")]
    [Tooltip("空なら VoiceInterviewClient.apiBaseUrl から ws://.../ws/voice/recv を組み立てる")]
    public string wsUrlOverride = "";
    [Tooltip("受信音声のサンプリングレート（実験者ページの送信形式と一致させる）")]
    public int networkSampleRate = 16000;
    [Tooltip("再生開始前に溜めるバッファ（秒）。小さいほど低遅延だが途切れやすい")]
    [Range(0.04f, 0.5f)]
    public float jitterBufferSeconds = 0.12f;
    [Tooltip("溜まりすぎたらこの長さまで古い音を捨てて遅延を詰める（秒）")]
    [Range(0.2f, 2f)]
    public float maxBufferSeconds = 0.5f;

    [Header("LocalMic")]
    [Tooltip("評価者マイクのデバイス名。空なら既定デバイス。使用可能なデバイスは起動時に Console に出力される")]
    public string micDevice = "";
    [Tooltip("マイクのサンプリングレート(Hz)")]
    public int sampleRate = 16000;
    [Tooltip("再生の遅延バッファ（秒）。小さいほど低遅延だが途切れやすい")]
    [Range(0.05f, 0.5f)]
    public float latencySeconds = 0.15f;
    [Tooltip("ON にすると自前で Microphone.Start せず、PerfectInterviewer が既に開いているマイクを共有する（検証用）")]
    public bool useSharedMic = false;

    [Header("誤頷き防止")]
    [Tooltip("評価者の声がこの音量を超えている間は、参加者マイクの入力を無視する")]
    public float suppressLevel = 0.01f;
    [Tooltip("評価者の声が止んでから参加者マイクの無視を解除するまでの猶予（秒）")]
    public float suppressHangover = 0.4f;

    [Header("デバッグ")]
    [Tooltip("Play 時に自動で中継を開始する（条件を起動せず単体テストしたいとき）")]
    public bool debugStartOnPlay = false;

    [Header("状態（読み取り専用）")]
    [SerializeField] private bool relaying = false;
    [SerializeField] private bool networkConnected = false;
    [SerializeField] private float bufferedSeconds = 0f;

    public bool IsRelaying => relaying;
    public bool NetworkConnected => networkConnected;
    public float BufferedSeconds => bufferedSeconds;
    /// <summary>直近に評価者の声（しきい値超え）を受け取ってからの秒数。</summary>
    public float SecondsSinceVoice => Time.realtimeSinceStartup - _lastVoiceRealtime;

    private AudioClip _clip;
    private bool _playbackStarted = false;
    private bool _suppressing = false;
    private float _lastVoiceRealtime = -999f;

    // LocalMic
    private string _device;
    private bool _ownsMic = false;

    // Network: 受信スレッド → 音声スレッドへ渡すリングバッファ
    private float[] _ring;
    private int _ringRead, _ringCount;
    private bool _priming = true;              // ジッタバッファが溜まるまで無音を出す
    private readonly object _lock = new object();
    private volatile float _peakSinceCheck;    // 受信音量のピーク（suppressListening 用）
    private CancellationTokenSource _cts;
    private Task _netTask;

    void Awake()
    {
        Debug.Log("[HumanVoiceRelay] 使用可能なマイク: " +
            (Microphone.devices.Length == 0 ? "(なし)" : string.Join(" / ", Microphone.devices)));
    }

    void Start()
    {
        if (shareFrom == null) shareFrom = FindObjectOfType<PerfectInterviewer>(true);
        if (voiceClient == null) voiceClient = FindObjectOfType<VoiceInterviewClient>(true);
        if (debugStartOnPlay)
        {
            Debug.LogWarning("[HumanVoiceRelay] Debug Start On Play が ON です（単体テスト用）。本番では OFF にしてください。");
            StartCoroutine(DebugAutoStart());
        }
    }

    private IEnumerator DebugAutoStart()
    {
        // アバターは SeatInterviewerAtTable により配置完了まで一時的に非アクティブになる。
        float t = 0f;
        while (t < 15f && !(voiceOutput != null && voiceOutput.isActiveAndEnabled))
        {
            t += Time.deltaTime;
            yield return null;
        }
        StartRelay();
    }

    // ============================================================
    // 開始 / 停止
    // ============================================================

    /// <summary>評価者の声 → VoiceOutput の中継を開始する（人間条件で呼ぶ）。</summary>
    [ContextMenu("中継を開始 (StartRelay)")]
    public void StartRelay()
    {
        if (relaying) return;
        if (voiceOutput == null)
        {
            Debug.LogError("[HumanVoiceRelay] voiceOutput 未設定。中継できません。");
            return;
        }

        if (source == RelaySource.Network) StartNetwork();
        else if (!StartLocalMic()) return;

        relaying = true;
        Debug.Log($"[HumanVoiceRelay] 中継開始（source={source}）");
    }

    /// <summary>中継を停止する（AI条件・セッション終了時に呼ぶ）。</summary>
    [ContextMenu("中継を停止 (StopRelay)")]
    public void StopRelay()
    {
        if (!relaying) return;
        relaying = false;

        // 中継音声を再生している場合だけ止める（他の音声の再生中なら触らない）
        if (voiceOutput != null && _clip != null && voiceOutput.clip == _clip)
        {
            if (voiceOutput.isPlaying) voiceOutput.Stop();
            voiceOutput.loop = false;
            voiceOutput.clip = null; // TTS 再生に戻せるようクリア
        }
        Cleanup();
        Debug.Log("[HumanVoiceRelay] 中継停止");
    }

    /// <summary>VoiceOutput には触らずに中継状態だけを解除する（他の再生を邪魔しないため）。</summary>
    private void ReleaseWithoutTouchingOutput(string reason)
    {
        relaying = false;
        Cleanup();
        Debug.LogWarning("[HumanVoiceRelay] " + reason);
    }

    private void Cleanup()
    {
        _playbackStarted = false;
        SetSuppress(false);
        StopNetwork();

        // 自前で開いたマイクだけ閉じる。参加者マイクと同じデバイスは絶対に閉じない
        // （Quest はマイクが1本なので、ここで End すると参加者の録音が以後すべて空になる）
        if (_ownsMic && !string.IsNullOrEmpty(_device) && Microphone.IsRecording(_device)
            && !IsParticipantMic(_device))
            Microphone.End(_device);
        _ownsMic = false;
    }

    private bool IsParticipantMic(string device) =>
        shareFrom != null && shareFrom.GetMicName() == device;

    // ============================================================
    // Network
    // ============================================================
    private string WsUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(wsUrlOverride)) return wsUrlOverride;
            string baseUrl = voiceClient != null ? voiceClient.apiBaseUrl : "http://127.0.0.1:8001";
            if (baseUrl.StartsWith("https://")) baseUrl = "wss://" + baseUrl.Substring(8);
            else if (baseUrl.StartsWith("http://")) baseUrl = "ws://" + baseUrl.Substring(7);
            return baseUrl.TrimEnd('/') + "/ws/voice/recv";
        }
    }

    private void StartNetwork()
    {
        lock (_lock)
        {
            _ring = new float[Mathf.CeilToInt(networkSampleRate * 2f)]; // 2秒分
            _ringRead = _ringCount = 0;
            _priming = true;
        }

        // 1秒のループ・ストリーミングクリップ。中身は OnAudioRead がリングバッファから供給する
        _clip = AudioClip.Create("HumanVoice", networkSampleRate, 1, networkSampleRate, true, OnAudioRead);
        voiceOutput.clip = _clip;
        voiceOutput.loop = true;
        voiceOutput.Play();
        _playbackStarted = true;

        _cts = new CancellationTokenSource();
        string url = WsUrl;
        string auth = ServerConfig.AuthorizationHeader; // Resources の読み込みはメインスレッドで行う
        _netTask = Task.Run(() => ReceiveLoop(url, auth, _cts.Token));
    }

    private void StopNetwork()
    {
        if (_cts != null)
        {
            // 受信タスクがまだトークンを参照しているので Dispose はしない（Cancel で抜ける）
            _cts.Cancel();
            _cts = null;
        }
        _netTask = null;
        networkConnected = false;
    }

    private async Task ReceiveLoop(string url, string auth, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            using (var ws = new ClientWebSocket())
            {
                if (auth != null) ws.Options.SetRequestHeader("Authorization", auth);
                try
                {
                    await ws.ConnectAsync(new Uri(url), ct);
                    networkConnected = true;
                    Debug.Log($"[HumanVoiceRelay] 音声受信に接続しました: {url}");

                    byte carry = 0;
                    bool hasCarry = false;
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        if (r.MessageType != WebSocketMessageType.Binary) continue;
                        PushPcm16(buf, r.Count, ref carry, ref hasCarry);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    if (!ct.IsCancellationRequested)
                        Debug.LogWarning($"[HumanVoiceRelay] 音声受信エラー（再接続します）: {e.Message}");
                }
            }
            networkConnected = false;
            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>16bit LE PCM をリングバッファへ積む（受信スレッド）。</summary>
    private void PushPcm16(byte[] data, int count, ref byte carry, ref bool hasCarry)
    {
        int cap = _ring.Length;
        int maxKeep = Mathf.Min(cap, Mathf.CeilToInt(maxBufferSeconds * networkSampleRate));
        float peak = 0f;

        lock (_lock)
        {
            int i = 0;
            while (i < count)
            {
                short v;
                if (hasCarry) { v = (short)(carry | (data[i] << 8)); hasCarry = false; i += 1; }
                else if (i + 1 < count) { v = (short)(data[i] | (data[i + 1] << 8)); i += 2; }
                else { carry = data[i]; hasCarry = true; break; }

                float f = v / 32768f;
                if (Mathf.Abs(f) > peak) peak = Mathf.Abs(f);

                if (_ringCount >= cap) { _ringRead = (_ringRead + 1) % cap; _ringCount--; }
                _ring[(_ringRead + _ringCount) % cap] = f;
                _ringCount++;
            }

            // 溜まりすぎ（受信が再生より速い・一時停止明けなど）→ 古い音を捨てて遅延を詰める
            if (_ringCount > maxKeep)
            {
                int drop = _ringCount - Mathf.CeilToInt(jitterBufferSeconds * networkSampleRate);
                _ringRead = (_ringRead + drop) % cap;
                _ringCount -= drop;
            }
        }
        if (peak > _peakSinceCheck) _peakSinceCheck = peak;
    }

    /// <summary>ストリーミングクリップの読み出し（音声スレッド）。足りなければ無音を出す。</summary>
    private void OnAudioRead(float[] data)
    {
        lock (_lock)
        {
            if (_ring == null) { Array.Clear(data, 0, data.Length); return; }
            int cap = _ring.Length;
            int prime = Mathf.CeilToInt(jitterBufferSeconds * networkSampleRate);
            if (_priming && _ringCount >= prime) _priming = false;

            for (int i = 0; i < data.Length; i++)
            {
                if (!_priming && _ringCount > 0)
                {
                    data[i] = _ring[_ringRead];
                    _ringRead = (_ringRead + 1) % cap;
                    _ringCount--;
                }
                else
                {
                    data[i] = 0f;
                    if (_ringCount == 0) _priming = true; // 途切れたら再び少し溜めてから鳴らす
                }
            }
        }
    }

    // ============================================================
    // LocalMic
    // ============================================================
    private bool StartLocalMic()
    {
        if (useSharedMic)
        {
            if (shareFrom == null || !shareFrom.IsMicReady())
            {
                Debug.LogError("[HumanVoiceRelay] 共有元（PerfectInterviewer）のマイクが未準備です。");
                return false;
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
                return false;
            }
            _device = string.IsNullOrEmpty(micDevice) ? Microphone.devices[0] : micDevice;
            if (IsParticipantMic(_device))
            {
                // 同じデバイスを Start し直すと参加者マイクの録音が壊れる
                Debug.LogError($"[HumanVoiceRelay] 評価者マイク '{_device}' が参加者マイクと同じです。" +
                               "Quest 単体運用では source=Network を使ってください。");
                return false;
            }
            _clip = Microphone.Start(_device, true, 1, sampleRate); // 1秒ループバッファ
            _ownsMic = true;
            if (_clip == null)
            {
                Debug.LogError($"[HumanVoiceRelay] マイク開始に失敗: {_device}");
                return false;
            }
        }
        StartCoroutine(BeginLocalPlayback());
        return true;
    }

    private IEnumerator BeginLocalPlayback()
    {
        // マイクが録音を開始するまで待つ（最大3秒）
        float until = Time.realtimeSinceStartup + 3f;
        while (relaying && Microphone.GetPosition(_device) <= 0)
        {
            if (Time.realtimeSinceStartup > until)
            {
                Debug.LogError($"[HumanVoiceRelay] マイク '{_device}' が録音を開始しません。");
                yield break;
            }
            yield return null;
        }
        if (!relaying) yield break;

        voiceOutput.clip = _clip;
        voiceOutput.loop = true;
        voiceOutput.timeSamples = 0;
        voiceOutput.Play();
        _playbackStarted = true;
    }

    // ============================================================
    // 毎フレーム
    // ============================================================
    void Update()
    {
        if (!relaying || _clip == null || voiceOutput == null) return;

        // 同じ AudioSource を AI の音声（TTS）など別の再生に使われた場合は、中継を手放す。
        if (_playbackStarted && voiceOutput.clip != _clip)
        {
            ReleaseWithoutTouchingOutput("VoiceOutput が別の音声の再生に使われたため、中継を停止しました。");
            return;
        }

        if (source == RelaySource.Network)
        {
            lock (_lock) bufferedSeconds = _ringCount / (float)networkSampleRate;
            float peak = _peakSinceCheck;
            _peakSinceCheck = 0f;
            if (peak > suppressLevel) _lastVoiceRealtime = Time.realtimeSinceStartup;
        }
        else
        {
            UpdateLocalMicSync();
        }

        // 評価者が話している間は参加者マイクを無視する（スピーカーの声で頷かないように）
        SetSuppress(SecondsSinceVoice <= suppressHangover);
    }

    private void SetSuppress(bool on)
    {
        if (on == _suppressing || shareFrom == null) return;
        _suppressing = on;
        shareFrom.suppressListening = on;
    }

    private void UpdateLocalMicSync()
    {
        if (!voiceOutput.isPlaying) return;

        // 再生位置がマイク書き込み位置から latency 分だけ後ろを追う状態に保つ（ドリフト補正）
        int clipLen = _clip.samples;
        if (clipLen <= 0) return;
        int micPos = Microphone.GetPosition(_device);
        if (micPos < 0) return;

        int target = Mathf.RoundToInt(latencySeconds * _clip.frequency);
        int playPos = voiceOutput.timeSamples;
        int trail = (((micPos - playPos) % clipLen) + clipLen) % clipLen;

        if (Mathf.Abs(trail - target) > _clip.frequency / 5)
        {
            int newPos = (((micPos - target) % clipLen) + clipLen) % clipLen;
            voiceOutput.timeSamples = Mathf.Clamp(newPos, 0, clipLen - 1);
        }

        // 音量（suppressListening 用）
        var tmp = new float[128];
        int pos = micPos - tmp.Length;
        if (pos >= 0)
        {
            _clip.GetData(tmp, pos);
            float peak = 0f;
            foreach (var f in tmp) peak = Mathf.Max(peak, Mathf.Abs(f));
            if (peak > suppressLevel) _lastVoiceRealtime = Time.realtimeSinceStartup;
        }
    }

    void OnDisable()
    {
        StopRelay();
    }

    void OnDestroy()
    {
        StopNetwork();
    }
}
