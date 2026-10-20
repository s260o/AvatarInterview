using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 実験者が PC ブラウザから実験を操作するためのリモート操作ブリッジ。
///
/// サーバー（Server/main.py）の POST /api/experiment/poll を一定間隔で叩き、
///   - 自分の状態（セッション・条件・残り時間・アバターの状態など）を報告し、
///   - 実験者ページ（http://サーバー:8001/experimenter）で押された操作コマンドを受け取って実行する。
/// ヘッドセット内には何も表示しないので、参加者に条件ラベルが見えることはない。
///
/// シーンへの配置は不要（ExperimentConditionManager があれば自動で同じ GameObject に追加される）。
/// </summary>
[DisallowMultipleComponent]
public class ExperimentRemoteControl : MonoBehaviour
{
    // ===== サーバーとの JSON（Server/main.py とフィールド名を一致させる） =====
    [Serializable]
    private class StatusReport
    {
        public string device;
        public string participant_id;
        public int group;
        public string order;
        public int session_number;     // 1..4（0 は未開始）
        public int session_count;
        public bool session_active;
        public bool all_sessions_done;
        public string condition_id;    // "A".."D"（未開始は "-"）
        public string evaluator;       // "AI" / "人間"
        public string nonverbal;       // "High" / "Low"
        public float remaining_seconds;
        public string instruction;
        public string[] seed_questions;      // WoZ の人間条件で評価者が読む質問（AutoAll では空）
        public string control_mode;          // auto（全条件自動・教示のみ変える）/ woz
        public string voice_state;     // Idle / Recording / Processing / Speaking
        public bool input_enabled;
        public bool interview_finished;
        public bool human_relay_active;
        public bool voice_stream_connected;   // 評価者音声の受信 WebSocket が繋がっているか
        public float voice_buffer_seconds;    // 受信音声のバッファ量
        public float voice_last_heard_seconds; // 評価者の声を最後に受け取ってからの秒数（-1 は未受信）
        public bool mic_recording;            // 参加者マイクが録音中か
        public string current_expression;     // 表示中の表情（落ち着いた顔に戻っていれば空）
        public string expression_policy;      // 表情の方針の版（supportive_v1 など）
        public int expression_event_id;       // 表情を適用するたびに増える番号（サーバーが変化を記録する）
        public string expression_source;      // ai / manual / editor
        public string expression_requested;   // 要求されたラベル
        public string expression_applied;     // 実際に適用したラベル（Low で抑制したときは空）
        public bool expression_replaced;      // 許可一覧にないため置き換えたか
        public bool expression_suppressed;    // Low 条件のため無表情にしたか
        public bool expression_resting;       // 反応を保ち終えて落ち着いた顔に戻っているか
        public string avatar_level;           // アバター側で実際に適用されている非言語レベル（High / Low）
        public string last_user_text;
        public string last_bot_text;
        public int last_command_id;
        public string last_command_result;
    }

    [Serializable]
    private class RemoteCommand
    {
        public int id;
        public string type;            // start_next / end_session / set_participant / reset_participant / recover_avatar
        public string participant_id;
        public int group;
        public string label;           // type="expression" のときの表情ラベル
    }

    [Serializable]
    private class PollResponse
    {
        public RemoteCommand[] commands;
    }

    [Header("参照")]
    public ExperimentConditionManager conditionManager;
    [Tooltip("サーバー URL の取得元。アバターの状態も報告する")]
    public VoiceInterviewClient voiceClient;
    public HumanVoiceRelay humanVoiceRelay;
    [Tooltip("表情ボタンの適用先")]
    public VrmExpressionApplier expressionApplier;

    [Header("通信")]
    [Tooltip("空なら VoiceInterviewClient.apiBaseUrl を使う")]
    public string apiBaseUrlOverride = "";
    [Tooltip("ポーリング間隔（秒）。表情ボタンの反応速度に直結する")]
    public float pollInterval = 0.25f;

    [Header("状態（読み取り専用）")]
    [SerializeField] private bool connected;
    [SerializeField] private int lastCommandId;
    [SerializeField] private string lastCommandResult = "";

    public bool Connected => connected;

    private string ApiBaseUrl =>
        !string.IsNullOrEmpty(apiBaseUrlOverride) ? apiBaseUrlOverride
        : voiceClient != null ? voiceClient.apiBaseUrl
        : "http://127.0.0.1:8001";

    // ExperimentConditionManager があるシーンでは自動で有効にする（シーン編集不要）
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoAttach()
    {
        if (FindObjectOfType<ExperimentRemoteControl>() != null) return;
        var manager = FindObjectOfType<ExperimentConditionManager>();
        if (manager != null) manager.gameObject.AddComponent<ExperimentRemoteControl>();
    }

    void Start()
    {
        if (conditionManager == null) conditionManager = GetComponent<ExperimentConditionManager>();
        if (conditionManager == null) conditionManager = FindObjectOfType<ExperimentConditionManager>();
        if (voiceClient == null) voiceClient = FindObjectOfType<VoiceInterviewClient>(true);
        if (humanVoiceRelay == null) humanVoiceRelay = FindObjectOfType<HumanVoiceRelay>(true);
        if (expressionApplier == null) expressionApplier = FindObjectOfType<VrmExpressionApplier>(true);

        if (conditionManager == null)
        {
            Debug.LogWarning("[RemoteControl] ExperimentConditionManager が見つからないため無効化します。");
            enabled = false;
            return;
        }
        Debug.Log($"[RemoteControl] 実験者ページ: {ApiBaseUrl}/experimenter");
        StartCoroutine(CoPollLoop());
    }

    private IEnumerator CoPollLoop()
    {
        var wait = new WaitForSecondsRealtime(pollInterval);
        while (true)
        {
            yield return CoPollOnce();
            yield return wait;
        }
    }

    private IEnumerator CoPollOnce()
    {
        string json = JsonUtility.ToJson(BuildStatus());

        using var req = new UnityWebRequest($"{ApiBaseUrl}/api/experiment/poll", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 5;
        ServerConfig.Authorize(req);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            if (req.responseCode == 401)
                Debug.LogError("[RemoteControl] 認証に失敗しました。Resources/ServerSettings.json の apiKey を確認してください。");
            else if (connected) Debug.LogWarning($"[RemoteControl] サーバーとの接続が切れました: {req.error}");
            connected = false;
            yield break;
        }
        if (!connected) Debug.Log("[RemoteControl] サーバーに接続しました。");
        connected = true;

        PollResponse res = null;
        try { res = JsonUtility.FromJson<PollResponse>(req.downloadHandler.text); }
        catch (Exception e) { Debug.LogError($"[RemoteControl] 応答の解析に失敗: {e.Message}"); }

        if (res?.commands == null) yield break;
        foreach (var cmd in res.commands) Execute(cmd);
    }

    // ============================================================
    // コマンド実行
    // ============================================================
    private void Execute(RemoteCommand cmd)
    {
        string result;
        var m = conditionManager;
        switch (cmd.type)
        {
            case "start_next":
                if (m.IsSessionActive) result = "無視: 実行中のセッションがあります";
                else if (m.AllSessionsDone) result = "無視: 全セッションが完了しています";
                else { m.StartNextSession(); result = $"OK: セッション {m.SessionNumber} を開始"; }
                break;

            case "end_session":
                if (!m.IsSessionActive) result = "無視: 実行中のセッションがありません";
                else { int n = m.SessionNumber; m.EndSession(); result = $"OK: セッション {n} を終了"; }
                break;

            case "set_participant":
                result = m.SetParticipant(cmd.participant_id, cmd.group)
                    ? $"OK: 参加者 {cmd.participant_id}（群{cmd.group}）を設定"
                    : "無視: セッション実行中は変更できません";
                break;

            case "recover_avatar":
                if (voiceClient == null) result = "無視: VoiceInterviewClient がありません";
                else { var before = voiceClient.CurrentState; voiceClient.ForceIdle(); result = $"OK: アバターを待機状態に戻しました（直前: {before}）"; }
                break;

            case "expression":
                if (expressionApplier == null) { result = "無視: VrmExpressionApplier がありません"; break; }
                {
                    string applied = expressionApplier.ApplyLabel(cmd.label, "manual");
                    if (expressionApplier.LastSuppressedByLow)
                        result = $"OK: 「{cmd.label}」を受け付けましたが、Low 条件のため無表情のままです";
                    else if (expressionApplier.LastReplaced)
                        result = $"OK: 「{cmd.label}」は使えない表情のため「{applied}」で表示しました";
                    else
                        result = $"OK: 表情「{applied}」";
                }
                break;

            case "reset_participant":
                if (m.IsSessionActive) result = "無視: セッション実行中はリセットできません";
                else { m.ResetParticipant(); result = "OK: 参加者の進行状況をリセット"; }
                break;

            default:
                result = $"無視: 未知のコマンド {cmd.type}";
                break;
        }

        lastCommandId = cmd.id;
        lastCommandResult = result;
        Debug.Log($"[RemoteControl] #{cmd.id} {cmd.type} → {result}");
    }

    // ============================================================
    // 状態報告
    // ============================================================
    private StatusReport BuildStatus()
    {
        var m = conditionManager;
        var c = m.CurrentCondition;
        var s = new StatusReport
        {
            device = SystemInfo.deviceModel,
            participant_id = m.participantId,
            group = m.group,
            order = m.CurrentOrder,
            session_number = m.SessionNumber,
            session_count = m.SessionCount,
            session_active = m.IsSessionActive,
            all_sessions_done = m.AllSessionsDone,
            condition_id = c.HasValue ? c.Value.id.ToString() : "-",
            evaluator = c.HasValue ? ExperimentConditionManager.EvaluatorDisplayName(c.Value.evaluator) : "",
            nonverbal = c.HasValue ? c.Value.nonverbal.ToString() : "",
            remaining_seconds = m.RemainingSeconds,
            instruction = c.HasValue ? m.InstructionFor(c.Value.evaluator) : "",
            control_mode = m.IsAutoAll ? "auto" : "woz",
            seed_questions = c.HasValue && !m.IsAutoAll && c.Value.evaluator == ExperimentConditionManager.Evaluator.Human
                ? m.CurrentSeedQuestions() : new string[0],
            human_relay_active = humanVoiceRelay != null && humanVoiceRelay.IsRelaying,
            voice_stream_connected = humanVoiceRelay != null && humanVoiceRelay.NetworkConnected,
            voice_buffer_seconds = humanVoiceRelay != null ? humanVoiceRelay.BufferedSeconds : 0f,
            voice_last_heard_seconds = humanVoiceRelay != null && humanVoiceRelay.SecondsSinceVoice < 3600f
                ? humanVoiceRelay.SecondsSinceVoice : -1f,
            last_command_id = lastCommandId,
            last_command_result = lastCommandResult,
            current_expression = expressionApplier != null && !expressionApplier.IsResting ? expressionApplier.LastAppliedLabel : "",
            expression_policy = VrmExpressionApplier.ExpressionPolicy,
            expression_event_id = expressionApplier != null ? expressionApplier.ExpressionEventId : 0,
            expression_source = expressionApplier != null ? expressionApplier.LastSource : "",
            expression_requested = expressionApplier != null ? expressionApplier.LastRequestedLabel : "",
            expression_applied = expressionApplier != null ? expressionApplier.LastAppliedLabel : "",
            expression_replaced = expressionApplier != null && expressionApplier.LastReplaced,
            expression_suppressed = expressionApplier != null && expressionApplier.LastSuppressedByLow,
            expression_resting = expressionApplier == null || expressionApplier.IsResting,
            avatar_level = expressionApplier != null ? expressionApplier.currentLevel.ToString() : "",
        };
        if (voiceClient != null)
        {
            s.voice_state = voiceClient.CurrentState.ToString();
            s.input_enabled = voiceClient.inputEnabled;
            s.interview_finished = voiceClient.InterviewFinished;
            s.last_user_text = voiceClient.GetLastUserText();
            s.last_bot_text = voiceClient.GetLastBotText();
            var pi = voiceClient.interviewer;
            s.mic_recording = pi != null && !string.IsNullOrEmpty(pi.GetMicName()) && Microphone.IsRecording(pi.GetMicName());
        }
        return s;
    }
}
