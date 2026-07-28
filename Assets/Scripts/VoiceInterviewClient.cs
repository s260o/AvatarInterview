using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 音声で面接を行うクライアント。
/// 右手トリガーを押している間だけ録音し、離すと
/// /api/stt → /api/interview → /api/tts を順に呼んでアバターに喋らせる。
///
/// マイクは PerfectInterviewer が起動したものを共有する（起動を一本化するため）。
/// 会話履歴はサーバー側（FastAPI の SESSION）が保持するので、こちらは毎回 1 発話だけ送る。
/// </summary>
public class VoiceInterviewClient : MonoBehaviour
{
    // ===== サーバー応答（InterviewClient.cs と型名が衝突しないよう別名にしている） =====
    [Serializable]
    private class VoiceApiRequest
    {
        public string message;
    }

    [Serializable]
    private class VoiceApiResponse
    {
        public string model;
        public string expression;
        public string text;
        public string raw;
    }

    [Serializable]
    private class SttResponse
    {
        public string text;
    }

    public enum State { Idle, Recording, Processing, Speaking }

    [Header("サーバー")]
    [Tooltip("エディタ: http://127.0.0.1:8001 / 実機: http://（PCのLAN IP）:8001")]
    public string apiBaseUrl = "http://127.0.0.1:8001";

    [Header("参照")]
    [Tooltip("マイクを起動している PerfectInterviewer（マイク共有元）")]
    public PerfectInterviewer interviewer;
    [Tooltip("TTS 音声の再生先")]
    public AudioSource audioSource;
    [Tooltip("表情ラベルを適用する VrmExpressionApplier")]
    public VrmExpressionApplier expressionApplier;

    [Header("操作")]
    [Tooltip("録音ボタン（押している間だけ録音）")]
    public OVRInput.Button talkButton = OVRInput.Button.PrimaryIndexTrigger;
    public OVRInput.Controller talkController = OVRInput.Controller.RTouch;
    [Tooltip("面接をリセットするボタン（左コントローラー Y）")]
    public OVRInput.Button resetButton = OVRInput.Button.Two;
    public OVRInput.Controller resetController = OVRInput.Controller.LTouch;

    [Header("録音")]
    [Tooltip("1 回の発話の最大録音長（秒）。暴走防止のための上限")]
    public float maxRecordSeconds = 45f;
    [Tooltip("これより短い録音は誤操作とみなして送信しない（秒）")]
    public float minRecordSeconds = 0.4f;

    [Header("状態（読み取り専用）")]
    [SerializeField] private State state = State.Idle;
    [SerializeField] private string lastUserText = "";
    [SerializeField] private string lastBotText = "";

    private int _recordStartPos = -1;
    private float _recordStartTime;

    public State CurrentState => state;
    public string GetLastUserText() => lastUserText;
    public string GetLastBotText() => lastBotText;

    void Reset()
    {
        interviewer = FindObjectOfType<PerfectInterviewer>();
        expressionApplier = FindObjectOfType<VrmExpressionApplier>();
        audioSource = GetComponent<AudioSource>();
    }

    void Start()
    {
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
        }
        if (interviewer == null)
            Debug.LogWarning("[VoiceInterviewClient] PerfectInterviewer が未設定です。マイクを共有できません。");
    }

    void Update()
    {
        // ── 録音開始 / 終了（Hold to Talk）──
        if (state == State.Idle && OVRInput.GetDown(talkButton, talkController))
            StartRecording();

        if (state == State.Recording)
        {
            // 上限に達したら自動で打ち切る
            if (Time.time - _recordStartTime >= maxRecordSeconds)
            {
                Debug.Log("[VoiceInterviewClient] 最大録音長に達したため送信します。");
                StopRecordingAndSend();
            }
            else if (OVRInput.GetUp(talkButton, talkController))
            {
                StopRecordingAndSend();
            }
        }

        // ── 面接リセット ──
        if (state == State.Idle && OVRInput.GetDown(resetButton, resetController))
            ResetInterview();

#if UNITY_EDITOR
        // エディタ確認用: Space 押しっぱなしで録音、R でリセット
        if (state == State.Idle && Input.GetKeyDown(KeyCode.Space)) StartRecording();
        if (state == State.Recording && Input.GetKeyUp(KeyCode.Space)) StopRecordingAndSend();
        if (state == State.Idle && Input.GetKeyDown(KeyCode.R)) ResetInterview();
#endif
    }

    // ============================================================
    // 録音
    // ============================================================
    private void StartRecording()
    {
        if (interviewer == null || !interviewer.IsMicReady())
        {
            Debug.LogWarning("[VoiceInterviewClient] マイクが利用できません。");
            return;
        }

        _recordStartPos = Microphone.GetPosition(interviewer.GetMicName());
        _recordStartTime = Time.time;
        state = State.Recording;
        Debug.Log("[VoiceInterviewClient] 録音開始");
    }

    private void StopRecordingAndSend()
    {
        float duration = Time.time - _recordStartTime;
        AudioClip mic = interviewer != null ? interviewer.GetMicClip() : null;

        if (mic == null || _recordStartPos < 0)
        {
            state = State.Idle;
            return;
        }

        int endPos = Microphone.GetPosition(interviewer.GetMicName());
        float[] segment = ExtractSegment(mic, _recordStartPos, endPos);
        _recordStartPos = -1;

        if (duration < minRecordSeconds || segment == null || segment.Length == 0)
        {
            Debug.Log($"[VoiceInterviewClient] 録音が短すぎるため送信しません（{duration:F2}s）");
            state = State.Idle;
            return;
        }

        byte[] wav = EncodeWav(segment, mic.channels, mic.frequency);
        Debug.Log($"[VoiceInterviewClient] 録音終了 {duration:F2}s / {wav.Length} bytes → 送信");
        StartCoroutine(CoProcess(wav));
    }

    /// <summary>
    /// リングバッファ状のマイク AudioClip から、開始位置〜終了位置の区間だけを取り出す。
    /// </summary>
    private float[] ExtractSegment(AudioClip clip, int startPos, int endPos)
    {
        int ch = Mathf.Max(1, clip.channels);
        int totalFrames = clip.samples;              // 1ch あたりのフレーム数
        if (totalFrames <= 0) return null;

        int frames = endPos - startPos;
        if (frames < 0) frames += totalFrames;       // 途中で先頭に巻き戻った場合
        if (frames <= 0) return null;

        // 念のため上限でクランプ
        frames = Mathf.Min(frames, totalFrames);

        float[] all = new float[totalFrames * ch];
        clip.GetData(all, 0);

        float[] seg = new float[frames * ch];
        int offset = startPos * ch;
        for (int i = 0; i < seg.Length; i++)
            seg[i] = all[(offset + i) % all.Length];

        return seg;
    }

    // ============================================================
    // メイン処理: STT → interview → TTS → 再生
    // ============================================================
    private IEnumerator CoProcess(byte[] wavBytes)
    {
        state = State.Processing;

        // 往復遅延の計測（実機検証用）。各段階の所要時間を Debug.Log に出す
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tStart = sw.ElapsedMilliseconds;

        // ---- ① /api/stt ----
        string userText = null;
        yield return CoSpeechToText(wavBytes, t => userText = t);
        long tStt = sw.ElapsedMilliseconds;

        if (string.IsNullOrWhiteSpace(userText))
        {
            Debug.LogWarning("[VoiceInterviewClient] 文字起こし結果が空でした。");
            state = State.Idle;
            yield break;
        }
        lastUserText = userText;
        Debug.Log($"[VoiceInterviewClient] You: {userText}");

        // ---- ② /api/interview ----
        VoiceApiResponse res = null;
        yield return CoInterview(userText, r => res = r);
        long tInterview = sw.ElapsedMilliseconds;

        if (res == null)
        {
            state = State.Idle;
            yield break;
        }
        lastBotText = res.text;
        Debug.Log($"[VoiceInterviewClient] Bot [{res.expression}]: {res.text}");

        // 表情を適用（ラベルはサーバー側で EmotionManager の一覧に合わせてある）
        if (expressionApplier != null && !string.IsNullOrEmpty(res.expression))
            expressionApplier.ApplyLabel(res.expression);

        // ---- ③ /api/tts ----
        AudioClip speech = null;
        yield return CoTextToSpeech(res.text, c => speech = c);
        long tTts = sw.ElapsedMilliseconds;

        // 往復遅延の内訳（発話終了→音声生成完了まで）。実機で数値を控えること
        Debug.Log($"[VoiceInterviewClient] 往復遅延  STT={tStt - tStart}ms  interview={tInterview - tStt}ms  TTS={tTts - tInterview}ms  合計(再生前)={tTts - tStart}ms");

        if (speech == null)
        {
            state = State.Idle;
            yield break;
        }

        // ---- ④ 再生（この間はマイク入力を無視して自分の声で頷かないようにする）----
        state = State.Speaking;
        if (interviewer != null) interviewer.suppressListening = true;

        audioSource.clip = speech;
        audioSource.Play();
        yield return new WaitWhile(() => audioSource.isPlaying);

        if (interviewer != null) interviewer.suppressListening = false;
        state = State.Idle;
    }

    private IEnumerator CoSpeechToText(byte[] wavBytes, Action<string> onDone)
    {
        var form = new List<IMultipartFormSection>
        {
            // フィールド名 "audio" は FastAPI 側の引数名と一致させる
            // 拡張子 .wav はサーバーがそのまま Whisper に渡すため必須
            new MultipartFormFileSection("audio", wavBytes, "audio.wav", "audio/wav")
        };

        using var req = UnityWebRequest.Post($"{apiBaseUrl}/api/stt", form);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[VoiceInterviewClient] STT 失敗: {req.error}\n{req.downloadHandler.text}");
            onDone(null);
            yield break;
        }

        var parsed = JsonUtility.FromJson<SttResponse>(req.downloadHandler.text);
        onDone(parsed != null ? parsed.text : null);
    }

    private IEnumerator CoInterview(string userText, Action<VoiceApiResponse> onDone)
    {
        // サーバーが履歴を保持するので、今回の 1 発話だけ送る
        string json = JsonUtility.ToJson(new VoiceApiRequest { message = userText });

        using var req = new UnityWebRequest($"{apiBaseUrl}/api/interview", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[VoiceInterviewClient] interview 失敗: {req.error}\n{req.downloadHandler.text}");
            onDone(null);
            yield break;
        }

        onDone(JsonUtility.FromJson<VoiceApiResponse>(req.downloadHandler.text));
    }

    private IEnumerator CoTextToSpeech(string text, Action<AudioClip> onDone)
    {
        // /api/tts は {"text": "..."} を期待する
        string json = "{\"text\":" + QuoteJsonString(text) + "}";

        using var req = new UnityWebRequest($"{apiBaseUrl}/api/tts", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[VoiceInterviewClient] TTS 失敗: {req.error}");
            onDone(null);
            yield break;
        }

        onDone(DecodeWav(req.downloadHandler.data));
    }

    /// <summary>面接をリセットする（サーバー側の会話履歴もクリアする）。</summary>
    public void ResetInterview()
    {
        StartCoroutine(CoReset());
    }

    private IEnumerator CoReset()
    {
        using var req = UnityWebRequest.PostWwwForm($"{apiBaseUrl}/api/reset", "");
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            lastUserText = "";
            lastBotText = "";
            Debug.Log("[VoiceInterviewClient] 面接をリセットしました。");
        }
        else
        {
            Debug.LogError($"[VoiceInterviewClient] リセット失敗: {req.error}");
        }
    }

    // ============================================================
    // WAV エンコード / デコード
    //   Unity には AudioClip <-> WAV の標準変換が無いため自前で用意する
    // ============================================================

    /// <summary>float PCM を 16bit PCM の WAV バイト列に変換する。</summary>
    private static byte[] EncodeWav(float[] samples, int channels, int frequency)
    {
        channels = Mathf.Max(1, channels);
        int dataSize = samples.Length * 2;               // 16bit = 2 bytes
        byte[] wav = new byte[44 + dataSize];

        void PutAscii(int at, string s)
        {
            for (int i = 0; i < s.Length; i++) wav[at + i] = (byte)s[i];
        }
        void PutInt(int at, int v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        void PutShort(int at, short v) => BitConverter.GetBytes(v).CopyTo(wav, at);

        PutAscii(0, "RIFF");
        PutInt(4, 36 + dataSize);
        PutAscii(8, "WAVE");
        PutAscii(12, "fmt ");
        PutInt(16, 16);                                   // fmt チャンクのサイズ
        PutShort(20, 1);                                  // PCM
        PutShort(22, (short)channels);
        PutInt(24, frequency);
        PutInt(28, frequency * channels * 2);             // バイトレート
        PutShort(32, (short)(channels * 2));              // ブロックアライン
        PutShort(34, 16);                                 // ビット深度
        PutAscii(36, "data");
        PutInt(40, dataSize);

        int p = 44;
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            wav[p++] = (byte)(v & 0xFF);
            wav[p++] = (byte)((v >> 8) & 0xFF);
        }
        return wav;
    }

    /// <summary>WAV バイト列を AudioClip に変換する（16bit PCM 前提）。</summary>
    private static AudioClip DecodeWav(byte[] wav)
    {
        if (wav == null || wav.Length < 44)
        {
            Debug.LogError("[VoiceInterviewClient] WAV データが不正です。");
            return null;
        }

        int channels = 1, sampleRate = 44100, bits = 16;
        int dataOffset = -1, dataSize = 0;

        // チャンクを走査する（LIST など余分なチャンクが挟まる場合に備える）
        int pos = 12;   // "RIFF" + size + "WAVE" の後ろから
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;

            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataSize = size;
                break;
            }

            if (size <= 0) break;
            pos = body + size + (size % 2);   // チャンクは偶数バイト境界
        }

        if (dataOffset < 0)
        {
            Debug.LogError("[VoiceInterviewClient] WAV に data チャンクが見つかりません。");
            return null;
        }
        if (bits != 16)
        {
            Debug.LogError($"[VoiceInterviewClient] 未対応のビット深度です: {bits}bit");
            return null;
        }

        dataSize = Mathf.Min(dataSize, wav.Length - dataOffset);
        int sampleCount = dataSize / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short v = (short)(wav[dataOffset + i * 2] | (wav[dataOffset + i * 2 + 1] << 8));
            samples[i] = v / (float)short.MaxValue;
        }

        channels = Mathf.Max(1, channels);
        var clip = AudioClip.Create("TTS", sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    /// <summary>文字列を JSON の文字列リテラルとして安全にエスケープする。</summary>
    private static string QuoteJsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
