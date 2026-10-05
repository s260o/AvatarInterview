import base64
import io
import json
import logging
import os
import time
import wave
from datetime import datetime
from pathlib import Path

from dotenv import load_dotenv
from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, Response
from google import genai
from pydantic import BaseModel

load_dotenv(Path(__file__).with_name(".env"), override=True)

GEMINI_API_KEY = (os.environ.get("GEMINI_API_KEY") or os.environ.get("GOOGLE_API_KEY") or "").strip()
if not GEMINI_API_KEY:
    raise RuntimeError(
        "GEMINI_API_KEY が設定されていません。"
        "Server/.env を作成してください（.env.example 参照）。"
    )
print(f"[startup] Using Gemini API key ...{GEMINI_API_KEY[-4:]} (len={len(GEMINI_API_KEY)})")

client = genai.Client(api_key=GEMINI_API_KEY)
# CHAT/TTS 用: SDK の自動リトライを切り、混雑時は待たずにフォールバックモデルへ回す
chat_client = genai.Client(
    api_key=GEMINI_API_KEY,
    http_options=genai.types.HttpOptions(retry_options=genai.types.HttpRetryOptions(attempts=0)),
)

STT_MODEL = os.environ.get("GEMINI_STT_MODEL", "gemini-3.5-transcribe")
CHAT_MODEL = os.environ.get("GEMINI_CHAT_MODEL", "gemini-3.5-flash-lite")
# 主モデルが混雑（503/429 等）・タイムアウトした時に順に試すモデル（カンマ区切り）
CHAT_FALLBACK_MODELS = [
    m.strip()
    for m in os.environ.get("GEMINI_CHAT_FALLBACK_MODELS", "gemini-3.1-flash-lite,gemini-3.7-flash").split(",")
    if m.strip() and m.strip() != CHAT_MODEL
]
# 1モデルあたりの待ち時間上限（秒）。超えたら次のモデルへ切り替える
CHAT_TIMEOUT_SEC = float(os.environ.get("GEMINI_CHAT_TIMEOUT_SEC", "10"))
TTS_MODEL = os.environ.get("GEMINI_TTS_MODEL", "gemini-3.8-flash-lite-tts")
TTS_FALLBACK_MODELS = [
    m.strip()
    for m in os.environ.get("GEMINI_TTS_FALLBACK_MODELS", "gemini-3.1-flash-tts-preview").split(",")
    if m.strip() and m.strip() != TTS_MODEL
]
TTS_TIMEOUT_SEC = float(os.environ.get("GEMINI_TTS_TIMEOUT_SEC", "15"))
TTS_VOICE = os.environ.get("GEMINI_TTS_VOICE", "Charon")
# 面接官の短い返答に深い思考は不要。レイテンシ優先で low を既定にする（gemini-3.7-flash は minimal 非対応）
CHAT_THINKING_LEVEL = os.environ.get("GEMINI_CHAT_THINKING_LEVEL", "low")

# Inline audio requests have a 20 MB total limit. Base64 overheadを考慮して14 MBに抑える。
MAX_AUDIO_BYTES = 14 * 1024 * 1024
AUDIO_MIME_BY_EXTENSION = {
    ".wav": "audio/wav",
    ".mp3": "audio/mp3",
    ".aiff": "audio/aiff",
    ".aif": "audio/aiff",
    ".aac": "audio/aac",
    ".ogg": "audio/ogg",
    ".flac": "audio/flac",
}
SUPPORTED_AUDIO_MIME_TYPES = set(AUDIO_MIME_BY_EXTENSION.values())

# ============================================================
# ロギング設定
#   logs/server_YYYYMMDD.log       … 人間が読むリクエストログ（コンソールにも出力）
#   logs/conversation_YYYYMMDD.jsonl … 面接の構造化ログ（実験データ／1行1ターン）
# ============================================================
LOG_DIR = Path(__file__).parent / "logs"
LOG_DIR.mkdir(exist_ok=True)

logger = logging.getLogger("avatar_interview")
logger.setLevel(logging.INFO)
if not logger.handlers:
    _fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")

    _file = logging.FileHandler(
        LOG_DIR / f"server_{datetime.now():%Y%m%d}.log", encoding="utf-8"
    )
    _file.setFormatter(_fmt)
    logger.addHandler(_file)

    _console = logging.StreamHandler()
    _console.setFormatter(_fmt)
    logger.addHandler(_console)


def log_conversation(record: dict) -> None:
    """面接1ターンを JSONL として追記保存する（実験データ用）。"""
    record = {"time": datetime.now().isoformat(timespec="seconds"), **record}
    path = LOG_DIR / f"conversation_{datetime.now():%Y%m%d}.jsonl"
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record, ensure_ascii=False) + "\n")


def audio_mime_type(audio: UploadFile) -> str:
    """Geminiが受け付けるMIMEタイプをアップロード情報から決定する。"""
    content_type = (audio.content_type or "").split(";", 1)[0].lower()
    if content_type in SUPPORTED_AUDIO_MIME_TYPES:
        return content_type

    extension = Path(audio.filename or "").suffix.lower()
    mime_type = AUDIO_MIME_BY_EXTENSION.get(extension)
    if mime_type:
        return mime_type

    raise HTTPException(
        status_code=415,
        detail="未対応の音声形式です。WAV、MP3、AIFF、AAC、OGG、FLACを使用してください。",
    )


def raise_gemini_error(operation: str, error: Exception) -> None:
    """Gemini SDKの例外を、クライアント向けの安全なHTTPエラーへ変換する。"""
    message = str(error)
    code = getattr(error, "code", None)
    logger.error("%s failed: %s", operation, message)

    if code == 429 or "RESOURCE_EXHAUSTED" in message:
        raise HTTPException(
            status_code=429,
            detail="Gemini APIの無料枠またはレート上限に達しました。しばらく待って再試行してください。",
        ) from error
    if code in (400, 401, 403) or "API_KEY_INVALID" in message:
        raise HTTPException(
            status_code=502,
            detail="Gemini APIキーまたはGoogle AI Studioのプロジェクト設定を確認してください。",
        ) from error
    raise HTTPException(
        status_code=502,
        detail=f"Gemini APIの{operation}処理に失敗しました。",
    ) from error


def pcm_to_wav(pcm: bytes, sample_rate: int = 24000) -> bytes:
    """Gemini TTSが返す16-bit mono PCMをUnity/ブラウザ向けWAVに包む。"""
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm)
    return buffer.getvalue()


app = FastAPI(title="AvatarInterview Backend")

# ブラウザデモ（別オリジン / file:// から開いた場合）でも叩けるように CORS を許可
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


# ============================================================
# GET / — ブラウザ用 面接デモ画面（Unity 不要のスタンドアロン）
# ============================================================
@app.get("/")
async def demo_page():
    return FileResponse(os.path.join(os.path.dirname(__file__), "demo.html"))


# ============================================================
# POST /api/stt — 音声 → テキスト（Gemini Transcribe）
# ============================================================
@app.post("/api/stt")
async def speech_to_text(audio: UploadFile = File(...)):
    audio_bytes = await audio.read()
    if not audio_bytes:
        raise HTTPException(status_code=400, detail="音声データが空です。")
    if len(audio_bytes) > MAX_AUDIO_BYTES:
        raise HTTPException(
            status_code=413,
            detail="音声データが大きすぎます。14 MB以下にしてください。",
        )

    mime_type = audio_mime_type(audio)
    encoded_audio = base64.b64encode(audio_bytes).decode("ascii")

    t0 = time.perf_counter()
    try:
        interaction = client.interactions.create(
            model=STT_MODEL,
            input=[
                {
                    "type": "audio",
                    "data": encoded_audio,
                    "mime_type": mime_type,
                }
            ],
            generation_config={
                "transcription_config": {
                    "language_codes": ["ja-JP"],
                    "mode": "verbatim",
                }
            },
            store=False,
        )
    except Exception as e:
        raise_gemini_error("STT", e)

    transcript = (interaction.output_text or "").strip()

    dt = time.perf_counter() - t0
    logger.info(
        "STT  model=%s  %d bytes (%s)  %.2fs  -> %r",
        STT_MODEL, len(audio_bytes), mime_type, dt, transcript,
    )
    log_conversation({
        "endpoint": "stt", "model": STT_MODEL,
        "audio_bytes": len(audio_bytes), "latency_sec": round(dt, 2),
        "transcript": transcript,
    })
    return {"text": transcript}


# ============================================================
# POST /api/interview — 会話履歴 → 応答テキスト＋表情ラベル（Gemini）
# Unity 側 ApiRequest / ApiResponse とフィールド名を一致させている
# ============================================================
class ApiMessage(BaseModel):
    role: str
    content: str


class ApiRequest(BaseModel):
    # 新方式: サーバー側の履歴に追加する「今回の1発話」だけを送る（サーバーが文脈を記憶）
    message: str | None = None
    # 旧方式: クライアントが全履歴を送る（ステートレス）。Unity InterviewClient 互換用に残す
    messages: list[ApiMessage] | None = None


# ============================================================
# 面接セッションの会話履歴（サーバー側メモリ）
#   プロセスが生きている間だけ保持され、FastAPI の起動/終了で自動的に消える。
#   面接をやり直したいときは POST /api/reset で手動クリアできる。
# ============================================================
SESSION: list[dict] = []


@app.on_event("startup")
async def _reset_on_startup():
    SESSION.clear()
    logger.info("=== セッション開始: 会話履歴をリセットしました ===")


@app.on_event("shutdown")
async def _reset_on_shutdown():
    SESSION.clear()
    logger.info("=== セッション終了: 会話履歴をリセットしました ===")


@app.post("/api/reset")
async def reset_session():
    n = len(SESSION)
    SESSION.clear()
    reset_interview_state()
    logger.info("=== 手動リセット: %d 件の履歴と面接進行状態をクリアしました ===", n)
    return {"status": "ok", "cleared_turns": n}


# EmotionManager.cs の _labels 配列と完全一致させること
EXPRESSION_LABELS = [
    "笑顔", "穏やかな笑顔", "にこやか",
    "穏やか", "落ち着いた", "真顔",
    "うなずき", "感心", "好意的な笑顔",
    "真剣", "少し疑問", "確認するような表情",
    "困り顔", "苦笑い", "眉をひそめる",
    "厳しい", "真剣（強め）", "沈黙（数秒）",
    "優しい", "受容的", "安心させる笑顔",
    "丁寧な笑顔", "感謝", "会釈",
]

SYSTEM_PROMPT = f"""あなたは就職面接を行う面接官です。
参加者の回答を聞き、自然な日本語の話し言葉で次の質問または相槌を1つだけ返してください。
返答はTTSで読み上げられるため、1〜3文程度で簡潔にしてください。

【面接の設定と制約】
- 設定は「大学卒業後の就職を想定した、一般的な企業の新卒採用一次面接」です。
  特定の企業・業界は設定されていません。
- したがって「志望動機」「なぜ当社を選んだか」「この業界についてどう思うか」など、
  特定の企業・業界の知識や就職活動の経験を前提とする質問は絶対にしないでください。
  （就職活動をまだ経験していない参加者も対象に含まれるためです）
- 質問は必ず、参加者自身のこれまでの経験・考え・人柄についてのものにしてください。
- 参加者の回答に対して評価・採点・良し悪しのコメントはしないでください。
  相槌と次の質問のみを返してください。

あわせて、あなたの今の表情として最もふさわしいものを次のラベル一覧から1つだけ選んでください。
ラベル一覧: {", ".join(EXPRESSION_LABELS)}

必ず次のJSON形式のみで出力してください（説明文や余計なテキストを含めないこと）:
{{"text": "発言内容", "expression": "ラベル名"}}
"""

INTERVIEW_RESPONSE_SCHEMA = {
    "type": "object",
    "properties": {
        "text": {"type": "string"},
        "expression": {"type": "string", "enum": EXPRESSION_LABELS},
    },
    "required": ["text", "expression"],
    "additionalProperties": False,
}


# ============================================================
# 準構造化面接（シード質問は固定・追質問は可変）
#   進行台本 interview_script_2026_0820.html の「シード質問セット」と一致させること。
#   シード質問と締めの台詞は LLM を通さず逐語で送出するため、
#   全参加者・全条件で完全に同一になる（人間条件のオペレータも同じ文を読む）。
# ============================================================
QUESTION_SETS: dict[int, list[str]] = {
    1: [
        "自己紹介をお願いします。",
        "学生時代に最も力を入れたことは何ですか。",
        "そこで直面した課題と、その対処を教えてください。",
    ],
    2: [
        "あなたの長所を一つ挙げてください。",
        "それが活きた具体的な経験を教えてください。",
        "逆に、改善したいと感じている点は何ですか。",
    ],
    3: [
        "これまでで最も達成感を得た経験は何ですか。",
        "その中であなたが果たした役割を教えてください。",
        "難しかった点を、どのように乗り越えましたか。",
    ],
    4: [
        "チームで取り組んだ経験を教えてください。",
        "意見が対立したとき、どうしましたか。",
        "その経験から学んだことを教えてください。",
    ],
}

# 予備質問: 主要シード3問を消化しても時間が余る場合に使う（短答の参加者向け）。
# 同じ話題に追質問を重ね続けるのは不自然なため、話題を変えて時間まで埋める。
RESERVE_QUESTIONS: dict[int, list[str]] = {
    1: [
        "その経験を通して、ご自身のどんなところが成長したと思いますか。",
        "周囲の方からは、どのような人だと言われることが多いですか。",
    ],
    2: [
        "その改善したい点について、いま取り組んでいることはありますか。",
        "これまでで、自分の力が一番発揮できたと感じた場面を教えてください。",
    ],
    3: [
        "その経験から得たことを、今どのように活かしていますか。",
        "逆に、うまくいかなかった経験があれば教えてください。",
    ],
    4: [
        "チームの中で、あなたはどのような役割を任されることが多いですか。",
        "人と協力するうえで、大切にしていることは何ですか。",
    ],
}

# 冒頭は簡潔に入る（1名が4回続けて受けるため、毎回の長いあいさつは不自然）
OPENING_PREFIX = "それでは、"
# 2問目以降のつなぎ
SEED_CONNECTOR = "では、"
# 締め: 短くしつつ「評価される」ことには必ず触れる
#   （直後の面接後アンケートで評価不安を測るため、評価の顕在性を保つ設計）
CLOSING_TEXT = (
    "ありがとうございました。以上で終了です。"
    "お答えいただいた内容や話し方などをもとに、総合的に評価されます。"
)
OPENING_EXPRESSION = "丁寧な笑顔"
SEED_EXPRESSION = "穏やかな笑顔"
CLOSING_EXPRESSION = "会釈"

PROBE_INSTRUCTION = (
    "直前の参加者の回答について、内容を掘り下げる追質問を1つだけしてください。"
    "新しい話題に移らず、いま話している経験の中身を具体的に尋ねてください。"
)

# 追質問の可変ルール
MIN_PROBES_PER_SEED = 1     # 各シードに必ず1回は追質問する
MAX_PROBES_PER_SEED = 3     # 時間が余っても1シードにこれ以上は積まない
MAX_PROBES_LAST_SEED = 5    # 最後のシードは時間を埋めるため多めに許容（上限）
CLOSING_RESERVE_SEC = 25.0  # 締めの台詞のために残しておく時間
PROBE_MARGIN_SEC = 35.0     # 追質問を1往復するのに最低限必要とみなす時間

INTERVIEW: dict = {
    "active": False,
    "finished": False,
    "question_set": 0,
    "questions": [],    # 主要シード + 予備質問 を連結したもの
    "n_main": 0,        # 先頭から何問が「必ず聞く」主要シードか
    "seed_index": 0,
    "probes": 0,        # 現在の質問で消化した追質問の数
    "start_time": 0.0,
    "duration": 420.0,
}


def reset_interview_state() -> None:
    INTERVIEW.update(
        active=False, finished=False, question_set=0, questions=[], n_main=0,
        seed_index=0, probes=0, start_time=0.0, duration=420.0,
    )


def remaining_seconds() -> float:
    if not INTERVIEW["start_time"]:
        return 0.0
    return INTERVIEW["duration"] - (time.monotonic() - INTERVIEW["start_time"])


def decide_next_move() -> str:
    """次に返すものを決める。'probe'（追質問）/ 'seed'（次の質問）/ 'closing'（締め）。

    方針:
      - 主要シードは必ず全問消化する（時間が押していれば追質問を飛ばして先へ進む）
      - 主要シードを終えたら「充填モード」に入り、追質問と予備質問で規定時間まで埋める
      - 残り時間が締めの分を切ったら終了する
    """
    rem = remaining_seconds()
    if rem <= CLOSING_RESERVE_SEC:
        return "closing"

    questions = INTERVIEW["questions"]
    n_main = max(1, INTERVIEW["n_main"])
    i = INTERVIEW["seed_index"]
    probes = INTERVIEW["probes"]

    # 追質問を1往復するだけの時間が残っているか
    can_probe = rem > CLOSING_RESERVE_SEC + PROBE_MARGIN_SEC

    # 主要シードの最後に到達したら充填モード
    if i >= n_main - 1:
        if not can_probe:
            return "closing"
        # まず同じ話題を上限まで掘り下げる
        if probes < MAX_PROBES_LAST_SEED:
            return "probe"
        # 上限に達したら予備質問へ話題を移す
        if i + 1 < len(questions):
            return "seed"
        # 予備も尽きたら追質問で時間まで継続する
        return "probe"

    # ── ここからは主要シードを時間内に消化するための配分 ──
    per_seed = INTERVIEW["duration"] / n_main
    elapsed = INTERVIEW["duration"] - rem
    seed_deadline = per_seed * (i + 1)

    # 持ち時間を使い切った → 残りの主要シードを消化するため先へ進む
    if elapsed >= seed_deadline:
        return "seed"
    if probes < MIN_PROBES_PER_SEED and can_probe:
        return "probe"
    if probes < MAX_PROBES_PER_SEED and can_probe and elapsed < seed_deadline - PROBE_MARGIN_SEC:
        return "probe"
    return "seed"


def is_transient_error(e: Exception) -> bool:
    """混雑・一時障害・タイムアウトなど、別モデルで再試行すべきエラーか。"""
    code = getattr(e, "status_code", None) or getattr(e, "code", None)
    if code in (408, 429, 500, 502, 503, 504):
        return True
    name = type(e).__name__.lower()
    msg = str(e).lower()
    return "timeout" in name or "timed out" in msg or "connect" in name or "503" in msg or "429" in msg


def generate_reply(history: list[dict], extra_instruction: str | None = None) -> dict:
    """Gemini に応答を1つ生成させる。例外はそのまま呼び出し側へ投げる。"""
    prompt = format_interview_history(history)
    if extra_instruction:
        prompt += "\n\n【追加指示】" + extra_instruction

    t0 = time.perf_counter()
    models = [CHAT_MODEL, *CHAT_FALLBACK_MODELS]
    for i, model in enumerate(models):
        try:
            interaction = chat_client.interactions.create(
                model=model,
                system_instruction=SYSTEM_PROMPT,
                input=prompt,
                response_format={
                    "type": "text",
                    "mime_type": "application/json",
                    "schema": INTERVIEW_RESPONSE_SCHEMA,
                },
                generation_config={"thinking_level": CHAT_THINKING_LEVEL},
                store=False,
                timeout=CHAT_TIMEOUT_SEC,
            )
            break
        except Exception as e:
            if i == len(models) - 1 or not is_transient_error(e):
                raise
            logger.warning(
                "CHAT model=%s unavailable after %.2fs (%s) -> fallback to %s",
                model, time.perf_counter() - t0, type(e).__name__, models[i + 1],
            )
    dt = time.perf_counter() - t0

    raw = interaction.output_text or ""
    try:
        parsed = json.loads(raw)
        text = str(parsed.get("text", "")).strip()
        expression = str(parsed.get("expression", "")).strip()
    except (json.JSONDecodeError, AttributeError):
        text = raw or ""
        expression = ""

    if expression not in EXPRESSION_LABELS:
        expression = "真顔"
    if not text:
        text = "すみません、もう一度お願いできますか。"

    usage = getattr(interaction, "usage", None)
    return {
        "text": text,
        "expression": expression,
        "raw": raw,
        "model": getattr(interaction, "model", None) or CHAT_MODEL,
        "in": getattr(usage, "total_input_tokens", None),
        "out": getattr(usage, "total_output_tokens", None),
        "tot": getattr(usage, "total_tokens", None),
        "dt": dt,
    }


def format_interview_history(history: list[dict]) -> str:
    """会話履歴をGeminiへ渡す読みやすい会話記録に変換する。"""
    labels = {"user": "参加者", "assistant": "面接官", "model": "面接官"}
    turns = [
        f"{labels.get(item['role'], item['role'])}: {item['content']}"
        for item in history
    ]
    return "以下の会話の続きとして、最新の参加者発言に返答してください。\n\n" + "\n".join(turns)


class StartRequest(BaseModel):
    """面接の開始。question_set は面接の回（1〜4回目）に対応する。"""
    question_set: int = 1
    duration_seconds: float = 420.0


@app.post("/api/interview/start")
async def interview_start(req: StartRequest):
    """面接を開始し、あいさつ＋シード質問1を逐語で返す（アバターから話し始めるため）。"""
    qs = req.question_set if req.question_set in QUESTION_SETS else 1
    main_seeds = list(QUESTION_SETS[qs])
    SESSION.clear()
    INTERVIEW.update(
        active=True,
        finished=False,
        question_set=qs,
        questions=main_seeds + list(RESERVE_QUESTIONS.get(qs, [])),
        n_main=len(main_seeds),
        seed_index=0,
        probes=0,
        start_time=time.monotonic(),
        duration=max(60.0, float(req.duration_seconds)),
    )

    text = OPENING_PREFIX + INTERVIEW["questions"][0]
    SESSION.append({"role": "assistant", "content": text})

    logger.info(
        "=== 面接開始: set=%d 想定%.0f秒 主要%d問(+予備%d問) === %r",
        qs, INTERVIEW["duration"], INTERVIEW["n_main"],
        len(INTERVIEW["questions"]) - INTERVIEW["n_main"], text,
    )
    log_conversation({
        "endpoint": "interview_start", "mode": "structured", "phase": "opening",
        "question_set": qs, "seed_index": 0,
        "duration_sec": INTERVIEW["duration"],
        "assistant_text": text, "expression": OPENING_EXPRESSION,
    })
    return {
        "model": "(fixed)",
        "expression": OPENING_EXPRESSION,
        "text": text,
        "raw": "",
        "phase": "opening",
        "is_final": False,
        "seed_index": 0,
        "question_set": qs,
        "remaining_seconds": round(remaining_seconds(), 1),
    }


async def structured_turn(message: str):
    """準構造化モードの1ターン。シード質問・締めは逐語、追質問のみ LLM。"""
    SESSION.append({"role": "user", "content": message})
    decision = decide_next_move()

    if decision == "probe":
        try:
            r = generate_reply(list(SESSION), PROBE_INSTRUCTION)
        except Exception as e:
            if SESSION and SESSION[-1]["role"] == "user":
                SESSION.pop()
            logger.error("Probe failed (user=%r)", message)
            raise_gemini_error("追質問", e)
        INTERVIEW["probes"] += 1
        text, expression, raw, model = r["text"], r["expression"], r["raw"], r["model"]
        in_tok, out_tok, tot_tok, dt = r["in"], r["out"], r["tot"], r["dt"]
        phase, is_final = "probe", False

    elif decision == "seed":
        INTERVIEW["seed_index"] += 1
        INTERVIEW["probes"] = 0
        text = SEED_CONNECTOR + INTERVIEW["questions"][INTERVIEW["seed_index"]]
        expression, raw, model = SEED_EXPRESSION, "", "(fixed)"
        in_tok = out_tok = tot_tok = None
        dt = 0.0
        phase, is_final = "seed", False

    else:  # closing
        text = CLOSING_TEXT
        expression, raw, model = CLOSING_EXPRESSION, "", "(fixed)"
        in_tok = out_tok = tot_tok = None
        dt = 0.0
        phase, is_final = "closing", True
        INTERVIEW["finished"] = True
        INTERVIEW["active"] = False

    SESSION.append({"role": "assistant", "content": text})
    rem = remaining_seconds()

    logger.info(
        "CHAT[%s] set=%d seed=%d probes=%d remain=%.0fs  user=%r -> [%s] %r",
        phase, INTERVIEW["question_set"], INTERVIEW["seed_index"],
        INTERVIEW["probes"], rem, message, expression, text,
    )
    log_conversation({
        "endpoint": "interview", "mode": "structured", "phase": phase,
        "question_set": INTERVIEW["question_set"],
        "seed_index": INTERVIEW["seed_index"],
        "probes_on_seed": INTERVIEW["probes"],
        "remaining_sec": round(rem, 1),
        "model": model, "latency_sec": round(dt, 2),
        "prompt_tokens": in_tok, "completion_tokens": out_tok, "total_tokens": tot_tok,
        "user_text": message, "assistant_text": text, "expression": expression,
    })
    return {
        "model": model,
        "expression": expression,
        "text": text,
        "raw": raw,
        "phase": phase,
        "is_final": is_final,
        "seed_index": INTERVIEW["seed_index"],
        "question_set": INTERVIEW["question_set"],
        "remaining_seconds": round(rem, 1),
    }


@app.post("/api/interview")
async def interview(req: ApiRequest):
    # ── 準構造化モード（/api/interview/start 済み）──────────────
    if INTERVIEW["active"] and not INTERVIEW["finished"] and req.message is not None:
        return await structured_turn(req.message)

    # ── 自由対話モード（demo.html / 旧 Unity クライアント互換）────
    if req.message is not None:
        # 新方式: サーバー側メモリに文脈を蓄積する
        server_memory = True
        SESSION.append({"role": "user", "content": req.message})
        history = list(SESSION)
        last_user = req.message
    elif req.messages:
        # 旧方式: クライアントが送ってきた履歴をそのまま使う（ステートレス）
        server_memory = False
        history = [{"role": m.role, "content": m.content} for m in req.messages]
        last_user = next(
            (m.content for m in reversed(req.messages) if m.role == "user"), ""
        )
    else:
        raise HTTPException(status_code=400, detail="message か messages のどちらかが必要です。")

    try:
        r = generate_reply(history)
    except Exception as e:
        # 失敗した場合はさっき積んだユーザー発話を巻き戻す（次回の文脈汚染を防ぐ）
        if server_memory and SESSION and SESSION[-1]["role"] == "user":
            SESSION.pop()
        logger.error("Interview failed (user=%r)", last_user)
        raise_gemini_error("面接応答", e)

    # 応答をサーバー側メモリに追記（新方式のみ）
    if server_memory:
        SESSION.append({"role": "assistant", "content": r["text"]})

    logger.info(
        "CHAT model=%s memory=%s turns=%d tokens(p/c/t)=%s/%s/%s %.2fs  user=%r -> [%s] %r",
        r["model"], server_memory, len(history),
        r["in"], r["out"], r["tot"],
        r["dt"], last_user, r["expression"], r["text"],
    )
    log_conversation({
        "endpoint": "interview", "mode": "free", "model": r["model"],
        "server_memory": server_memory, "history_turns": len(history),
        "latency_sec": round(r["dt"], 2),
        "prompt_tokens": r["in"],
        "completion_tokens": r["out"],
        "total_tokens": r["tot"],
        "user_text": last_user, "assistant_text": r["text"], "expression": r["expression"],
    })

    return {
        "model": r["model"],
        "expression": r["expression"],
        "text": r["text"],
        "raw": r["raw"],
        "phase": "free",
        "is_final": False,
        "seed_index": -1,
        "question_set": 0,
        "remaining_seconds": -1.0,
    }


# ============================================================
# POST /api/tts — テキスト → 音声（Gemini TTS）
# ============================================================
class TtsRequest(BaseModel):
    text: str


@app.post("/api/tts")
async def text_to_speech(req: TtsRequest):
    if not req.text.strip():
        raise HTTPException(status_code=400, detail="text が空です。")

    t0 = time.perf_counter()
    models = [TTS_MODEL, *TTS_FALLBACK_MODELS]
    for i, model in enumerate(models):
        try:
            interaction = chat_client.interactions.create(
                model=model,
                input=req.text,
                response_format={"type": "audio"},
                generation_config={
                    "speech_config": [
                        {"voice": TTS_VOICE},
                    ]
                },
                store=False,
                timeout=TTS_TIMEOUT_SEC,
            )
            break
        except Exception as e:
            if i < len(models) - 1 and is_transient_error(e):
                logger.warning(
                    "TTS  model=%s unavailable after %.2fs (%s) -> fallback to %s",
                    model, time.perf_counter() - t0, type(e).__name__, models[i + 1],
                )
                continue
            logger.error("TTS failed (%d chars)", len(req.text))
            raise_gemini_error("TTS", e)
    dt = time.perf_counter() - t0

    output_audio = interaction.output_audio
    if not output_audio or not output_audio.data:
        logger.error("TTS returned no audio (%d chars)", len(req.text))
        raise HTTPException(status_code=502, detail="Gemini APIから音声が返されませんでした。")

    encoded_pcm = output_audio.data
    pcm = base64.b64decode(encoded_pcm) if isinstance(encoded_pcm, str) else bytes(encoded_pcm)
    audio = pcm_to_wav(pcm)
    logger.info(
        "TTS  model=%s voice=%s  %d chars -> %d bytes  %.2fs  text=%r",
        model, TTS_VOICE, len(req.text), len(audio), dt, req.text,
    )
    log_conversation({
        "endpoint": "tts", "model": model, "voice": TTS_VOICE,
        "chars": len(req.text), "audio_bytes": len(audio),
        "latency_sec": round(dt, 2), "text": req.text,
    })

    return Response(content=audio, media_type="audio/wav")
