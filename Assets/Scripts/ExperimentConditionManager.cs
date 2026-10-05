using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 実験条件マネージャ（2×2・被験者内）。
///   Factor A: 評価主体ラベル（AI / 人間） … 実験者が口頭で教示（本パネルが台詞を提示）
///   Factor B: 非言語行動（High / Low）     … EmotionManager.ApplyPreset で一括適用
///
/// 4条件：A=AI×High / B=AI×Low / C=人間×High / D=人間×Low
/// 提示順はカウンターバランス（参加者を4群に均等配置）。
/// 各セッション約7分。開始時にサーバーの会話履歴をリセット（/api/reset）。
///
/// ※ 操作パネル(OnGUI)は Editor 限定。実機ビルドでは参加者の視界に評価主体ラベルが
///   映らないよう描画しない。本番は参加者に見えない画面から実験者が操作すること。
/// </summary>
[DisallowMultipleComponent]
public class ExperimentConditionManager : MonoBehaviour
{
    public enum Evaluator { AI, Human }

    [Serializable]
    public struct Condition
    {
        public char id;                                   // 'A'..'D'
        public Evaluator evaluator;                        // Factor A
        public VrmExpressionApplier.NonverbalLevel nonverbal; // Factor B
    }

    // ── カウンターバランス（4群の提示順）──
    private static readonly string[] ORDERS =
    {
        "ABDC", // 群1
        "BCAD", // 群2
        "CDBA", // 群3
        "DACB", // 群4
    };

    // ── シード質問（人間条件のオペレータが読み上げる用の表示）──
    //    Server/main.py の QUESTION_SETS と必ず一致させること。
    //    AI条件ではサーバーが同じ文をそのまま発話する。
    private static readonly string[][] SEED_QUESTIONS =
    {
        new[] { "自己紹介をお願いします。",
                "学生時代に最も力を入れたことは何ですか。",
                "そこで直面した課題と、その対処を教えてください。" },
        new[] { "あなたの長所を一つ挙げてください。",
                "それが活きた具体的な経験を教えてください。",
                "逆に、改善したいと感じている点は何ですか。" },
        new[] { "これまでで最も達成感を得た経験は何ですか。",
                "その中であなたが果たした役割を教えてください。",
                "難しかった点を、どのように乗り越えましたか。" },
        new[] { "チームで取り組んだ経験を教えてください。",
                "意見が対立したとき、どうしましたか。",
                "その経験から学んだことを教えてください。" },
    };

    // ── 4条件の定義 ──
    private Dictionary<char, Condition> _map;

    [Header("参照")]
    [Tooltip("非言語プリセットを一括適用する EmotionManager（VrmExpressionApplier）")]
    public VrmExpressionApplier emotionManager;
    [Tooltip("/api/reset を叩くための VoiceInterviewClient（AI条件で使用）")]
    public VoiceInterviewClient voiceClient;
    [Tooltip("人間条件（WoZ）で評価者音声を中継する HumanVoiceRelay")]
    public HumanVoiceRelay humanVoiceRelay;

    [Header("参加者設定")]
    public string participantId = "P01";
    [Range(1, 4)]
    [Tooltip("カウンターバランス群（1〜4）。提示順が決まる")]
    public int group = 1;

    [Header("セッション")]
    [Tooltip("1セッションの想定長（秒）。既定 420 = 7分")]
    public float sessionSeconds = 420f;
    [Tooltip("予定時間に達したら自動でセッションを終了する。既定 false（回答の途中切りを避け、実験者が手動終了）")]
    public bool autoEndOnTimeUp = false;

    [Header("操作パネル（Editor のみ表示）")]
    public bool showExperimenterPanel = true;

    [Header("状態（読み取り専用）")]
    [SerializeField] private int currentIndex = -1;    // 0..3。-1 は未開始
    [SerializeField] private bool sessionActive = false;
    [SerializeField] private string currentConditionId = "-";

    private float _sessionEndTime;
    private bool _timeUpNotified;
    private DateTime _sessionStartUtc;

    // ── 公開アクセサ ──
    public int SessionNumber => currentIndex + 1;               // 1..4
    public bool IsSessionActive => sessionActive;
    public string CurrentOrder => ORDERS[Mathf.Clamp(group, 1, 4) - 1];
    public float RemainingSeconds => sessionActive ? Mathf.Max(0f, _sessionEndTime - Time.time) : 0f;

    void Reset()
    {
        emotionManager = FindObjectOfType<VrmExpressionApplier>();
        voiceClient = FindObjectOfType<VoiceInterviewClient>();
        humanVoiceRelay = FindObjectOfType<HumanVoiceRelay>();
    }

    void Awake()
    {
        _map = new Dictionary<char, Condition>
        {
            { 'A', new Condition { id = 'A', evaluator = Evaluator.AI,    nonverbal = VrmExpressionApplier.NonverbalLevel.High } },
            { 'B', new Condition { id = 'B', evaluator = Evaluator.AI,    nonverbal = VrmExpressionApplier.NonverbalLevel.Low  } },
            { 'C', new Condition { id = 'C', evaluator = Evaluator.Human, nonverbal = VrmExpressionApplier.NonverbalLevel.High } },
            { 'D', new Condition { id = 'D', evaluator = Evaluator.Human, nonverbal = VrmExpressionApplier.NonverbalLevel.Low  } },
        };
    }

    void Start()
    {
        if (emotionManager == null) emotionManager = FindObjectOfType<VrmExpressionApplier>();
        if (voiceClient == null) voiceClient = FindObjectOfType<VoiceInterviewClient>();
        if (humanVoiceRelay == null) humanVoiceRelay = FindObjectOfType<HumanVoiceRelay>();
    }

    void Update()
    {
        if (sessionActive && !_timeUpNotified && Time.time >= _sessionEndTime)
        {
            _timeUpNotified = true;
            Debug.Log("[Experiment] 予定時間（約7分）に到達しました。区切りの良いところでセッションを終了してください。");
            if (autoEndOnTimeUp) EndSession();
        }
    }

    // ============================================================
    // セッション制御
    // ============================================================

    /// <summary>新しい参加者用に状態を初期化する。</summary>
    public void ResetParticipant()
    {
        sessionActive = false;
        currentIndex = -1;
        currentConditionId = "-";
        Debug.Log($"[Experiment] 参加者 {participantId}（群{group}・順 {CurrentOrder}）で初期化しました。");
    }

    /// <summary>カウンターバランス順の次のセッションを開始する。</summary>
    public void StartNextSession() => StartSessionAt(currentIndex + 1);

    /// <summary>指定インデックス（0..3）のセッションを開始する。</summary>
    public void StartSessionAt(int index)
    {
        string order = CurrentOrder;
        if (index < 0 || index >= order.Length)
        {
            Debug.LogWarning($"[Experiment] セッション番号が範囲外です（index={index}）。全 {order.Length} セッション。");
            return;
        }
        if (sessionActive)
        {
            Debug.LogWarning("[Experiment] 実行中のセッションがあります。先に EndSession してください。");
            return;
        }

        currentIndex = index;
        Condition c = _map[order[index]];
        currentConditionId = c.id.ToString();

        ApplyCondition(c);

        _sessionStartUtc = DateTime.UtcNow;
        _sessionEndTime = Time.time + sessionSeconds;
        _timeUpNotified = false;
        sessionActive = true;

        Debug.Log($"[Experiment] ▶ セッション {SessionNumber}/4 開始 — 条件 {c.id}（評価主体={EvaluatorLabel(c.evaluator)} / 非言語={c.nonverbal}）");
        Debug.Log($"[Experiment] ★実験者が読み上げる教示: 「{InstructionText(c.evaluator)}」");
        if (c.evaluator == Evaluator.Human)
        {
            Debug.Log("[Experiment] ⚠ 人間条件です。評価者が映像を観察・採点できる状態にしてから面接を開始してください。");
            Debug.Log("[Experiment] ★評価者が読み上げるシード質問（この順で）:" + SeedQuestionsText());
        }
    }

    /// <summary>現在のセッションを終了し、ログを記録する。</summary>
    public void EndSession()
    {
        if (!sessionActive)
        {
            Debug.LogWarning("[Experiment] 実行中のセッションがありません。");
            return;
        }
        sessionActive = false;

        // 人間条件の音声中継が動いていれば止める
        if (humanVoiceRelay != null && humanVoiceRelay.IsRelaying)
            humanVoiceRelay.StopRelay();

        string order = CurrentOrder;
        Condition c = _map[order[currentIndex]];
        LogSession(c, _sessionStartUtc, DateTime.UtcNow);

        Debug.Log($"[Experiment] ■ セッション {SessionNumber}/4 終了 — 条件 {c.id}");
        if (SessionNumber >= order.Length)
            Debug.Log("[Experiment] 全4セッションが完了しました。");
    }

    private void ApplyCondition(Condition c)
    {
        // Factor B: 非言語プリセットを一括適用（PerfectInterviewer・UdpReceiver へ伝播）
        if (emotionManager != null)
            emotionManager.ApplyPreset(c.nonverbal, applyToOthers: true);
        else
            Debug.LogWarning("[Experiment] emotionManager 未設定のため非言語プリセットを適用できません。");

        // Factor A: 評価主体に応じて面接の駆動経路を切り替える
        bool human = (c.evaluator == Evaluator.Human);

        if (voiceClient != null)
        {
            // 人間条件では AI の Hold-to-Talk 入力を止める（参加者は自然に話し、評価者が肉声で応じる）
            voiceClient.inputEnabled = !human;

            if (human)
            {
                // WoZ: AI パイプラインは使わないので、サーバーの履歴だけクリアする
                voiceClient.ResetInterview();
            }
            else
            {
                // AI条件: 面接を開始する。サーバーが「あいさつ＋シード質問1」を返し、
                // アバターから話し始める。シード質問は面接の回（1〜4）で固定。
                voiceClient.StartInterview(SessionNumber, sessionSeconds);
            }
        }
        else
        {
            Debug.LogWarning("[Experiment] voiceClient 未設定のため入力切替・面接開始を行えません。");
        }

        if (humanVoiceRelay != null)
        {
            if (human) humanVoiceRelay.StartRelay();  // 評価者マイク → VoiceOutput
            else humanVoiceRelay.StopRelay();
        }
        else if (human)
        {
            Debug.LogWarning("[Experiment] humanVoiceRelay 未設定のため人間音声を中継できません。");
        }
    }

    // ============================================================
    // 教示テキスト（Factor A）
    // ============================================================

    /// <summary>現在の面接の回に対応するシード質問（人間条件のオペレータ用）。</summary>
    public string[] CurrentSeedQuestions()
    {
        int idx = Mathf.Clamp(SessionNumber - 1, 0, SEED_QUESTIONS.Length - 1);
        return SEED_QUESTIONS[idx];
    }

    private string SeedQuestionsText()
    {
        var qs = CurrentSeedQuestions();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < qs.Length; i++) sb.Append("  ").Append(i + 1).Append(". ").Append(qs[i]);
        return sb.ToString();
    }

    private static string EvaluatorLabel(Evaluator e) => e == Evaluator.AI ? "AI" : "人間";

    /// <summary>実験者が読み上げる評価主体の教示。AI/人間で評価主体だけが異なり、他は同一。</summary>
    private static string InstructionText(Evaluator e) => e == Evaluator.AI
        ? "この回の面接では、あなたの回答内容・話し方・声の調子・表情などを、AI（人工知能）が自動で分析し、評価します。準備ができたら、右手のトリガーを押して話し始めてください。"
        : "この回の面接では、あなたの回答内容・話し方・声の調子・表情などを、別室にいる評価者（人）が、映像を通してその場で見て評価します。準備ができたら、右手のトリガーを押して話し始めてください。";

    // ============================================================
    // ログ（CSV）
    // ============================================================

    private void LogSession(Condition c, DateTime startUtc, DateTime endUtc)
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "experiment_logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"experiment_{participantId}.csv");
            bool isNew = !File.Exists(path);

            using (var sw = new StreamWriter(path, append: true, new UTF8Encoding(true)))
            {
                if (isNew)
                    sw.WriteLine("participant_id,group,order,session_index,condition,evaluator,nonverbal,start_utc,end_utc,duration_sec");
                double dur = (endUtc - startUtc).TotalSeconds;
                sw.WriteLine($"{participantId},{group},{CurrentOrder},{SessionNumber},{c.id},{c.evaluator},{c.nonverbal},{startUtc:o},{endUtc:o},{dur:F1}");
            }
            Debug.Log($"[Experiment] ログ保存: {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Experiment] ログ保存に失敗: {e.Message}");
        }
    }

    // ============================================================
    // 実験者用パネル（Editor のみ）
    // ============================================================
#if UNITY_EDITOR
    void OnGUI()
    {
        if (!showExperimenterPanel) return;

        GUI.skin.label.richText = true; // <b> タグを太字として描画する

        const int w = 360;
        GUILayout.BeginArea(new Rect(Screen.width - w - 12, 12, w, 320), GUI.skin.box);
        GUILayout.Label("<b>実験条件マネージャ</b>");

        GUILayout.Label($"参加者: {participantId}   群: {group}   順: {CurrentOrder}");

        string order = CurrentOrder;
        if (currentIndex >= 0 && currentIndex < order.Length)
        {
            Condition c = _map != null ? _map[order[currentIndex]] : default;
            GUILayout.Label($"セッション: {SessionNumber} / {order.Length}");
            GUILayout.Label($"条件: <b>{c.id}</b>  評価主体: <b>{EvaluatorLabel(c.evaluator)}</b>  非言語: <b>{c.nonverbal}</b>");

            if (sessionActive)
            {
                float rem = RemainingSeconds;
                GUI.color = (rem <= 0f) ? Color.red : Color.white;
                GUILayout.Label($"残り時間: {Mathf.FloorToInt(rem / 60f):00}:{Mathf.FloorToInt(rem % 60f):00}");
                GUI.color = Color.white;
            }

            GUILayout.Space(4);
            GUILayout.Label("読み上げる教示:");
            GUILayout.Label(InstructionText(c.evaluator), GUI.skin.textArea);

            // 人間条件（WoZ）では、評価者が読む質問をその場で確認できるようにする
            if (c.evaluator == Evaluator.Human)
            {
                GUILayout.Space(4);
                GUILayout.Label($"<b>評価者が読む質問（{SessionNumber}回目）</b>");
                var qs = CurrentSeedQuestions();
                for (int i = 0; i < qs.Length; i++)
                    GUILayout.Label($"{i + 1}. {qs[i]}", GUI.skin.textArea);
                GUILayout.Label("※ 各質問のあと、内容を掘り下げる追質問を入れて時間を調整する", GUI.skin.label);
            }
        }
        else
        {
            GUILayout.Label("未開始。［最初のセッション開始］を押してください。");
        }

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (!sessionActive)
        {
            if (GUILayout.Button(currentIndex < 0 ? "最初のセッション開始" : "次のセッション開始"))
                StartNextSession();
        }
        else
        {
            if (GUILayout.Button("セッション終了")) EndSession();
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("参加者リセット")) ResetParticipant();
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
#endif
}
