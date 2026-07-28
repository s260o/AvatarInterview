import json
import logging
import os
import tempfile
import time
from datetime import datetime
from pathlib import Path

from dotenv import load_dotenv
from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, Response
from openai import OpenAI
from pydantic import BaseModel

load_dotenv()

OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY")
if not OPENAI_API_KEY:
    raise RuntimeError("OPENAI_API_KEY が設定されていません。.env を作成してください（.env.example 参照）。")

client = OpenAI(api_key=OPENAI_API_KEY)

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
# POST /api/stt — 音声 → テキスト（Whisper）
# ============================================================
@app.post("/api/stt")
async def speech_to_text(audio: UploadFile = File(...)):
    audio_bytes = await audio.read()
    if not audio_bytes:
        raise HTTPException(status_code=400, detail="音声データが空です。")

    # アップロードされたファイルの拡張子を維持する（ブラウザは .webm、Unity は .wav）
    # Whisper は拡張子でフォーマットを判定するため、これを揃えないと失敗する
    _, ext = os.path.splitext(audio.filename or "")
    if not ext:
        ext = ".wav"

    with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as tmp:
        tmp.write(audio_bytes)
        tmp_path = tmp.name

    t0 = time.perf_counter()
    try:
        with open(tmp_path, "rb") as f:
            transcript = client.audio.transcriptions.create(
                model="whisper-1",
                file=f,
                language="ja",
            )
    except Exception as e:
        logger.error("STT failed (%s, %d bytes): %s", ext, len(audio_bytes), e)
        raise
    finally:
        os.remove(tmp_path)

    dt = time.perf_counter() - t0
    logger.info(
        "STT  model=whisper-1  %d bytes (%s)  %.2fs  -> %r",
        len(audio_bytes), ext, dt, transcript.text,
    )
    log_conversation({
        "endpoint": "stt", "model": "whisper-1",
        "audio_bytes": len(audio_bytes), "latency_sec": round(dt, 2),
        "transcript": transcript.text,
    })
    return {"text": transcript.text}


# ============================================================
# POST /api/interview — 会話履歴 → 応答テキスト＋表情ラベル（ChatGPT）
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
    logger.info("=== 手動リセット: %d 件の履歴をクリアしました ===", n)
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

あわせて、あなたの今の表情として最もふさわしいものを次のラベル一覧から1つだけ選んでください。
ラベル一覧: {", ".join(EXPRESSION_LABELS)}

必ず次のJSON形式のみで出力してください（説明文や余計なテキストを含めないこと）:
{{"text": "発言内容", "expression": "ラベル名"}}
"""


@app.post("/api/interview")
async def interview(req: ApiRequest):
    # ── 履歴の組み立て ──────────────────────────────
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

    messages = [{"role": "system", "content": SYSTEM_PROMPT}] + history

    t0 = time.perf_counter()
    try:
        completion = client.chat.completions.create(
            model="gpt-4o-mini",
            messages=messages,
            response_format={"type": "json_object"},
        )
    except Exception as e:
        # 失敗した場合はさっき積んだユーザー発話を巻き戻す（次回の文脈汚染を防ぐ）
        if server_memory and SESSION and SESSION[-1]["role"] == "user":
            SESSION.pop()
        logger.error("Interview failed (user=%r): %s", last_user, e)
        raise
    dt = time.perf_counter() - t0

    raw = completion.choices[0].message.content

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

    # 応答をサーバー側メモリに追記（新方式のみ）
    if server_memory:
        SESSION.append({"role": "assistant", "content": text})

    usage = completion.usage
    logger.info(
        "CHAT model=%s memory=%s turns=%d tokens(p/c/t)=%s/%s/%s %.2fs  user=%r -> [%s] %r",
        completion.model, server_memory, len(history),
        usage.prompt_tokens, usage.completion_tokens, usage.total_tokens,
        dt, last_user, expression, text,
    )
    log_conversation({
        "endpoint": "interview", "model": completion.model,
        "server_memory": server_memory, "history_turns": len(history),
        "latency_sec": round(dt, 2),
        "prompt_tokens": usage.prompt_tokens,
        "completion_tokens": usage.completion_tokens,
        "total_tokens": usage.total_tokens,
        "user_text": last_user, "assistant_text": text, "expression": expression,
    })

    return {
        "model": completion.model,
        "expression": expression,
        "text": text,
        "raw": raw,
    }


# ============================================================
# POST /api/tts — テキスト → 音声（OpenAI TTS）
# ============================================================
class TtsRequest(BaseModel):
    text: str


@app.post("/api/tts")
async def text_to_speech(req: TtsRequest):
    if not req.text.strip():
        raise HTTPException(status_code=400, detail="text が空です。")

    t0 = time.perf_counter()
    try:
        speech = client.audio.speech.create(
            model="tts-1",
            voice="onyx",
            input=req.text,
            response_format="wav",
        )
    except Exception as e:
        logger.error("TTS failed (%d chars): %s", len(req.text), e)
        raise
    dt = time.perf_counter() - t0

    audio = speech.content
    logger.info(
        "TTS  model=tts-1 voice=onyx  %d chars -> %d bytes  %.2fs  text=%r",
        len(req.text), len(audio), dt, req.text,
    )
    log_conversation({
        "endpoint": "tts", "model": "tts-1", "voice": "onyx",
        "chars": len(req.text), "audio_bytes": len(audio),
        "latency_sec": round(dt, 2), "text": req.text,
    })

    return Response(content=audio, media_type="audio/wav")
