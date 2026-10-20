import asyncio
import base64
import concurrent.futures
import hashlib
import hmac
import html
import io
import json
import logging
import os
import struct
import time
import wave
from datetime import datetime
from pathlib import Path
from urllib.parse import quote

from dotenv import load_dotenv
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse, Response
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

# Gemini 呼び出しに「全体の」制限時間をかけるための実行スレッド。
# SDK の timeout は「受信が途切れた時間」の上限なので、少しずつデータが返り続けると
# いつまでも終わらない（実際に TTS が無限に待ち続けた）。ここで締め切りを強制する。
_GEMINI_POOL = concurrent.futures.ThreadPoolExecutor(max_workers=16, thread_name_prefix="gemini")


def call_with_deadline(fn, deadline_sec: float, **kwargs):
    """fn(**kwargs) を別スレッドで実行し、deadline_sec を超えたら TimeoutError を投げる。
    超過したスレッドは裏で走り続けるが、結果は捨てる。"""
    future = _GEMINI_POOL.submit(fn, **kwargs)
    try:
        return future.result(timeout=deadline_sec)
    except concurrent.futures.TimeoutError:
        raise TimeoutError(f"Gemini call exceeded {deadline_sec:.0f}s deadline") from None

STT_MODEL = os.environ.get("GEMINI_STT_MODEL", "gemini-3.5-transcribe")
# STT の待ち時間上限（秒）。混雑時に SDK のリトライで数分固まるのを防ぐ
STT_TIMEOUT_SEC = float(os.environ.get("GEMINI_STT_TIMEOUT_SEC", "20"))
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

# ============================================================
# オープンラボモード（OPEN_LAB_MODE=1）
#   将来の実験参加者になりうる学生に見せるためのデモ設定。
#   実験の操作内容（評価主体の教示・WoZ・非言語の切り替え・本番の質問・評価の告知）は一切出さない。
#   詳しい方針は Server/OPEN_LAB.md を参照。
# ============================================================
OPEN_LAB_MODE = os.environ.get("OPEN_LAB_MODE", "0").strip().lower() in ("1", "true", "yes", "on")
# デモ1回の長さ（秒）。この時間を過ぎた次の発話で締めの台詞を返して終了する
OPEN_LAB_DURATION_SEC = float(os.environ.get("OPEN_LAB_DURATION_SEC", "180"))
# 来場者の発話内容をログに残すか。来場者は研究参加の同意をしていないため、既定は残さない
OPEN_LAB_SAVE_TEXT = os.environ.get("OPEN_LAB_SAVE_TEXT", "0").strip().lower() in ("1", "true", "yes", "on")


def _redact(text) -> str:
    """オープンラボでは来場者の発話（とそれを受けた応答）をログに書かない。"""
    if not OPEN_LAB_MODE or OPEN_LAB_SAVE_TEXT:
        return text
    return f"<{len(text or '')}文字・非記録>"

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


_TEXT_FIELDS = ("user_text", "assistant_text", "transcript", "text")


def log_conversation(record: dict) -> None:
    """面接1ターンを JSONL として追記保存する（実験データ用）。
    オープンラボモードでは実験データと混ざらないよう別ファイルに書き、発話内容は既定で残さない。"""
    record = {"time": datetime.now().isoformat(timespec="seconds"), **record}
    if "expression" in record or "requested" in record:
        record["expression_policy"] = EXPRESSION_POLICY_VERSION
    name = "conversation"
    if OPEN_LAB_MODE:
        name = "openlab"
        if not OPEN_LAB_SAVE_TEXT:
            record = {k: v for k, v in record.items() if k not in _TEXT_FIELDS}
    path = LOG_DIR / f"{name}_{datetime.now():%Y%m%d}.jsonl"
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


C2PA_MAGIC = b"C2PA"


def strip_c2pa_trailer(pcm: bytes) -> bytes:
    """Gemini TTS の音声末尾に付く C2PA マニフェストを取り除く。

    Gemini は生成した音声の末尾に、AI 生成物であることを示す来歴情報（C2PA）を
    「b"C2PA" + 4バイトのペイロード長（リトルエンディアン）+ ペイロード」の形で付けて返す。
    これを PCM として再生すると、末尾に約0.1秒の大音量の雑音として聞こえる。
    末尾にぴったり収まっている場合だけ除去し、それ以外は手を付けない。
    """
    idx = pcm.rfind(C2PA_MAGIC)
    while idx != -1:
        if idx + 8 <= len(pcm):
            length = struct.unpack_from("<I", pcm, idx + 4)[0]
            if idx + 8 + length == len(pcm):
                return pcm[: idx - (idx % 2)]  # 16bit のサンプル境界にそろえる
        idx = pcm.rfind(C2PA_MAGIC, 0, idx)
    return pcm


def pcm_to_wav(pcm: bytes, sample_rate: int = 24000) -> bytes:
    """Gemini TTSが返す16-bit mono PCMをUnity/ブラウザ向けWAVに包む（末尾の C2PA は除去する）。"""
    pcm = strip_c2pa_trailer(pcm)
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
# 認証（Cloudflare Tunnel で外部公開するため）
#   .env の API_TOKEN を合言葉にする。
#   - Unity など: ヘッダー "Authorization: Bearer <API_TOKEN>"（または "X-API-Key"）
#   - ブラウザ:   /login でパスワード（= API_TOKEN）を入れると Cookie が付く
#   - このPCから直接（http://localhost:8001）のアクセスは認証不要
#     ※ Tunnel 経由の通信も cloudflared から localhost に届くが、Cloudflare が必ず
#       CF-Connecting-IP ヘッダーを付けるので、それの有無で区別する
# ============================================================
API_TOKEN = os.environ.get("API_TOKEN", "").strip()
AUTH_COOKIE = "ai_auth"
AUTH_PUBLIC_PATHS = {"/login", "/favicon.ico"}
AUTH_HTML_PAGES = {"/", "/experimenter"}
LOGIN_MAX_FAILS = 5          # この回数失敗したら
LOGIN_LOCK_SEC = 600         # この秒数ログインを受け付けない（IP ごと）
_login_fails: dict[str, list[float]] = {}

if not API_TOKEN:
    logger.warning("API_TOKEN が未設定のため認証は無効です。外部に公開する場合は必ず設定してください。")


def _session_value() -> str:
    # Cookie にはトークンそのものではなく、トークンから作った値を入れる
    return hmac.new(API_TOKEN.encode(), b"avatar-interview-session", hashlib.sha256).hexdigest()


def _client_ip(headers, client) -> str:
    return headers.get("cf-connecting-ip") or (client.host if client else "?")


def _is_local_direct(headers, client) -> bool:
    return (client is not None and client.host in ("127.0.0.1", "::1")
            and "cf-connecting-ip" not in headers)


def _is_authorized(headers, cookies, client) -> bool:
    if not API_TOKEN or _is_local_direct(headers, client):
        return True
    auth = headers.get("authorization", "")
    token = auth[7:].strip() if auth.lower().startswith("bearer ") else headers.get("x-api-key", "")
    if token and hmac.compare_digest(token.encode(), API_TOKEN.encode()):
        return True
    cookie = cookies.get(AUTH_COOKIE, "")
    return bool(cookie) and hmac.compare_digest(cookie.encode(), _session_value().encode())


@app.middleware("http")
async def require_auth(request: Request, call_next):
    path = request.url.path
    if (request.method == "OPTIONS" or path in AUTH_PUBLIC_PATHS
            or _is_authorized(request.headers, request.cookies, request.client)):
        return await call_next(request)
    if request.method == "GET" and path in AUTH_HTML_PAGES:
        return RedirectResponse(f"/login?next={quote(path)}", status_code=303)
    return JSONResponse({"detail": "認証が必要です。"}, status_code=401)


async def ws_authorized(ws: WebSocket) -> bool:
    """WebSocket はミドルウェアを通らないので、各エンドポイントの先頭で確認する。"""
    if _is_authorized(ws.headers, ws.cookies, ws.client):
        return True
    await ws.close(code=4401, reason="unauthorized")
    return False


def _safe_next(next_path: str) -> str:
    # 外部サイトへのリダイレクトに使われないよう、サイト内のパスだけ許可する
    return next_path if next_path.startswith("/") and not next_path.startswith("//") else "/experimenter"


LOGIN_PAGE = """<!doctype html><html lang="ja"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>ログイン</title>
<style>body{{margin:0;min-height:100vh;display:grid;place-items:center;background:#f4f5f7;
font:15px/1.6 system-ui,"Hiragino Sans","Yu Gothic UI","Meiryo",sans-serif;color:#1d2330}}
form{{background:#fff;border:1px solid #e3e6eb;border-radius:10px;padding:24px 28px;width:min(340px,90vw)}}
h1{{font-size:17px;margin:0 0 14px}}input{{width:100%;box-sizing:border-box;font:inherit;padding:8px 10px;
border:1px solid #d1d5db;border-radius:6px;margin:4px 0 14px}}button{{width:100%;font:inherit;font-weight:600;
padding:10px;border:0;border-radius:8px;background:#2563eb;color:#fff;cursor:pointer}}
.err{{color:#b91c1c;font-size:14px;margin:0 0 10px}}</style></head><body>
<form method="post" action="/login"><h1>AvatarInterview</h1>{error}
<label for="pw">パスワード</label><input id="pw" name="password" type="password" autofocus required>
<input type="hidden" name="next" value="{next}"><button>ログイン</button></form></body></html>"""


@app.get("/login")
async def login_page(next: str = "/experimenter"):
    return HTMLResponse(LOGIN_PAGE.format(error="", next=html.escape(_safe_next(next))))


@app.post("/login")
async def login(request: Request, password: str = Form(""), next: str = Form("/experimenter")):
    ip = _client_ip(request.headers, request.client)
    now = time.time()
    fails = [t for t in _login_fails.get(ip, []) if now - t < LOGIN_LOCK_SEC]
    if len(fails) >= LOGIN_MAX_FAILS:
        logger.warning("LOGIN locked (ip=%s)", ip)
        return HTMLResponse(LOGIN_PAGE.format(
            error='<p class="err">失敗が続いたため、しばらくログインできません。</p>', next=html.escape(_safe_next(next))),
            status_code=429)

    if not API_TOKEN or not hmac.compare_digest(password.encode(), API_TOKEN.encode()):
        fails.append(now)
        _login_fails[ip] = fails
        logger.warning("LOGIN failed (ip=%s, %d/%d)", ip, len(fails), LOGIN_MAX_FAILS)
        return HTMLResponse(LOGIN_PAGE.format(
            error='<p class="err">パスワードが違います。</p>', next=html.escape(_safe_next(next))), status_code=401)

    _login_fails.pop(ip, None)
    logger.info("LOGIN ok (ip=%s)", ip)
    resp = RedirectResponse(_safe_next(next), status_code=303)
    resp.set_cookie(
        AUTH_COOKIE, _session_value(), max_age=30 * 24 * 3600, httponly=True, samesite="lax",
        secure="cf-connecting-ip" in request.headers,  # Tunnel 経由（https）のときだけ Secure
    )
    return resp


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
        interaction = await asyncio.to_thread(
            call_with_deadline,
            chat_client.interactions.create,
            STT_TIMEOUT_SEC,
            model=STT_MODEL,
            timeout=STT_TIMEOUT_SEC,
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
        STT_MODEL, len(audio_bytes), mime_type, dt, _redact(transcript),
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
    if OPEN_LAB_MODE:
        logger.warning(
            "=== オープンラボモードで起動: デモ%.0f秒・実験者ページ/音声中継は無効・発話内容のログ=%s ===",
            OPEN_LAB_DURATION_SEC, "記録する" if OPEN_LAB_SAVE_TEXT else "記録しない",
        )
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
    OPEN_LAB_FREE["start"] = 0.0
    logger.info("=== 手動リセット: %d 件の履歴と面接進行状態をクリアしました ===", n)
    return {"status": "ok", "cleared_turns": n}


# ============================================================
# 実験者リモート操作
#   実験者は PC のブラウザで GET /experimenter を開いて操作する。
#   Quest（ExperimentRemoteControl.cs）は POST /api/experiment/poll を定期的に叩き、
#   自分の状態を報告しつつ、溜まっている操作コマンドを受け取って実行する。
#   参加者のヘッドセットには条件ラベル等を一切表示しないための仕組み。
# ============================================================
EXPERIMENT_COMMAND_TYPES = {"start_next", "end_session", "set_participant", "reset_participant", "recover_avatar", "expression"}
QUEST_TIMEOUT_SEC = 3.0  # これ以上ポーリングが無ければ「未接続」とみなす

EXPERIMENT = {
    "pending": [],       # Quest 未取得のコマンド
    "next_id": 1,
    "quest_status": None,  # Quest から最後に報告された状態
    "last_seen": 0.0,      # 最後にポーリングが来た時刻（time.time()）
}


class ExperimentCommand(BaseModel):
    type: str
    participant_id: str | None = None
    group: int | None = None
    label: str | None = None  # type="expression" のときの表情ラベル


@app.get("/experimenter")
async def experimenter_page():
    if OPEN_LAB_MODE:
        raise HTTPException(status_code=404, detail="オープンラボモードでは使えません。")
    return FileResponse(os.path.join(os.path.dirname(__file__), "experimenter.html"))


@app.post("/api/experiment/command")
async def experiment_command(cmd: ExperimentCommand):
    if OPEN_LAB_MODE:
        raise HTTPException(status_code=404, detail="オープンラボモードでは使えません。")
    if cmd.type not in EXPERIMENT_COMMAND_TYPES:
        raise HTTPException(status_code=400, detail=f"未知のコマンドです: {cmd.type}")
    if cmd.type == "set_participant":
        if not (cmd.participant_id or "").strip():
            raise HTTPException(status_code=400, detail="参加者IDが空です。")
        if cmd.group not in (1, 2, 3, 4):
            raise HTTPException(status_code=400, detail="群は 1〜4 で指定してください。")
    if cmd.type == "expression" and cmd.label not in EXPRESSION_LABELS:
        raise HTTPException(status_code=400, detail=f"未知の表情ラベルです: {cmd.label}")

    # Unity の JsonUtility は null を扱えないため空文字 / 0 で埋める
    entry = {
        "id": EXPERIMENT["next_id"],
        "type": cmd.type,
        "participant_id": (cmd.participant_id or "").strip(),
        "group": cmd.group or 0,
        "label": cmd.label or "",
    }
    EXPERIMENT["next_id"] += 1
    EXPERIMENT["pending"].append(entry)
    logger.info("EXPERIMENT command queued: %s", entry)
    if cmd.type == "expression":
        # 人間条件でオペレーターが出した表情も、AI条件の表情と同じログに残す（分析用）
        quest = EXPERIMENT["quest_status"] or {}
        log_conversation({
            "endpoint": "experiment", "type": "manual_expression_requested", "expression": cmd.label,
            "participant_id": quest.get("participant_id"), "session_number": quest.get("session_number"),
            "condition_id": quest.get("condition_id"),
        })
    return {"status": "queued", "command": entry}


def _log_expression_event(status: dict) -> None:
    """Quest が報告した「表情の適用結果」が新しくなっていたら記録する。
    要求されたラベル・実際に適用したラベル・置き換えや Low による抑制の有無を区別して残す。
    （0.25秒ごとの報告なので、その間に2回以上適用された場合は最後の1回だけが残る）"""
    event_id = status.get("expression_event_id") or 0
    prev = (EXPERIMENT["quest_status"] or {}).get("expression_event_id") or 0
    if not event_id or event_id == prev:
        return
    log_conversation({
        "endpoint": "experiment", "type": "expression_applied",
        "source": status.get("expression_source"),
        "requested": status.get("expression_requested"),
        "applied": status.get("expression_applied"),
        "replaced": status.get("expression_replaced"),
        "suppressed_by_low": status.get("expression_suppressed"),
        "avatar_level": status.get("avatar_level"),
        "participant_id": status.get("participant_id"), "session_number": status.get("session_number"),
        "condition_id": status.get("condition_id"),
    })


@app.post("/api/experiment/poll")
async def experiment_poll(status: dict):
    """Quest から状態を受け取り、未取得のコマンドを渡す（渡したものはキューから消す）。"""
    if OPEN_LAB_MODE:
        # Unity 側がエラーを出し続けないよう、空の応答だけ返す（状態も保存しない）
        return {"commands": []}
    _log_expression_event(status)
    EXPERIMENT["quest_status"] = status
    EXPERIMENT["last_seen"] = time.time()
    commands, EXPERIMENT["pending"] = EXPERIMENT["pending"], []
    if commands:
        logger.info("EXPERIMENT commands delivered to Quest: %s", [c["id"] for c in commands])
    return {"commands": commands}


@app.get("/api/experiment/expressions")
async def experiment_expressions():
    if OPEN_LAB_MODE:
        raise HTTPException(status_code=404, detail="オープンラボモードでは使えません。")
    return {"groups": EXPRESSION_GROUPS, "policy": EXPRESSION_POLICY_VERSION}


@app.get("/api/experiment/status")
async def experiment_status():
    if OPEN_LAB_MODE:
        raise HTTPException(status_code=404, detail="オープンラボモードでは使えません。")
    age = time.time() - EXPERIMENT["last_seen"] if EXPERIMENT["last_seen"] else None
    return {
        "connected": age is not None and age < QUEST_TIMEOUT_SEC,
        "last_seen_sec": None if age is None else round(age, 1),
        "pending": EXPERIMENT["pending"],
        "quest": EXPERIMENT["quest_status"],
        "voice": {
            "sender_connected": VOICE["sender"] is not None,
            "receivers": len(VOICE["receivers"]),
        },
    }


# ============================================================
# 評価者音声の中継（人間条件 / WoZ）
#   実験者ページ（ブラウザ）が /ws/voice/send に 16kHz・16bit・mono の PCM を送り、
#   サーバーはそれを /ws/voice/recv に繋いでいる Quest（HumanVoiceRelay.cs）へそのまま流す。
#   Quest 側は人間条件で中継中のときだけ再生し、それ以外は捨てる。
# ============================================================
VOICE = {"sender": None, "receivers": set()}


@app.websocket("/ws/voice/send")
async def voice_send(ws: WebSocket):
    if not await ws_authorized(ws):
        return
    if OPEN_LAB_MODE:
        # 人条件（WoZ）の仕組みは見せない
        await ws.close(code=4403, reason="disabled in open lab mode")
        return
    await ws.accept()
    if VOICE["sender"] is not None:
        # 送信元は1つだけ（2つ目のタブなどで二重に流れるのを防ぐ）
        await ws.close(code=4000, reason="already sending")
        return
    VOICE["sender"] = ws
    logger.info("VOICE sender connected (receivers=%d)", len(VOICE["receivers"]))
    try:
        while True:
            chunk = await ws.receive_bytes()
            dead = []
            for r in list(VOICE["receivers"]):
                try:
                    await r.send_bytes(chunk)
                except Exception:
                    dead.append(r)
            for r in dead:
                VOICE["receivers"].discard(r)
    except WebSocketDisconnect:
        pass
    finally:
        if VOICE["sender"] is ws:
            VOICE["sender"] = None
        logger.info("VOICE sender disconnected")


@app.websocket("/ws/voice/recv")
async def voice_recv(ws: WebSocket):
    if not await ws_authorized(ws):
        return
    if OPEN_LAB_MODE:
        # 人条件（WoZ）の仕組みは見せない
        await ws.close(code=4403, reason="disabled in open lab mode")
        return
    await ws.accept()
    VOICE["receivers"].add(ws)
    logger.info("VOICE receiver connected (total=%d)", len(VOICE["receivers"]))
    try:
        # 受信側は何も送ってこない。切断の検知のためだけに待つ
        while (await ws.receive())["type"] != "websocket.disconnect":
            pass
    except (WebSocketDisconnect, RuntimeError):
        pass
    finally:
        VOICE["receivers"].discard(ws)
        logger.info("VOICE receiver disconnected (total=%d)", len(VOICE["receivers"]))


# EmotionManager.cs の _labels 配列と完全一致させること
# 表情ラベル（分類つき）。EmotionManager.cs の _labels と同じ順・同じ文字列にすること。
# 実験者ページの表情ボタンもこの分類で並ぶ。
# 表情の方針の版。ログに残し、どの方針で出た表情かを後から区別できるようにする
#   supportive_v1（2026-10-20）: High を「支持的で、発話に適切に反応する傾聴行動」と定義。
#   評価・賞賛・批判に見える表情（怪訝な顔・しかめっ面・呆れ・笑い・感心など）は実験中に出さない。
#   経緯は output/hypothesis_review/仮説の変遷と先行研究の再点検_2026-10-20.md の8〜9節。
EXPRESSION_POLICY_VERSION = "supportive_v1"

# 実験中に出してよい表情（分類つき）。EmotionManager.cs の _labels と同じ順・同じ文字列にすること。
# 実験者ページの表情ボタンもこの分類で並ぶ。最終候補はモデル上の見え方を確認して決める。
EXPRESSION_GROUPS: dict[str, list[str]] = {
    "基本の傾聴": ["穏やか", "落ち着いた", "穏やかな笑顔"],
    "関心・共感": ["興味津々", "優しい", "受容的", "真剣"],
    "あいさつ": ["丁寧な笑顔", "会釈"],
}
EXPRESSION_LABELS = [label for labels in EXPRESSION_GROUPS.values() for label in labels]
# 想定外のラベルが来たときに使う表情
DEFAULT_EXPRESSION = "穏やか"

# 表情の選び方（追質問・シード質問の両方で使う）
#   表情は High 条件でのみアバターに反映される（Low 条件では Unity 側で無表情に固定）。
EXPRESSION_GUIDE = f"""【表情の選び方】
表情は、参加者の話を聞き、受け止めていることを伝えるための傾聴反応です。
回答の良し悪し・能力・採用の可能性を評価する合図にはしないでください。賞賛も批判も表情で示しません。
- 普段の回答 → 穏やか / 穏やかな笑顔 / 落ち着いた
- もっと聞きたくなる話、具体的に話してくれたとき → 興味津々
- つらかった・大変だった話、失敗や悩みの話 → 真剣 / 優しい（笑顔にはしない）
- 迷いながら話している、うまく言葉にできていないとき → 受容的 / 優しい
- 話がずれている・わかりにくい回答でも、困惑や批判の表情は出さず → 穏やか / 真剣
常に笑顔にする必要はありません。大げさな反応も避けてください。
ラベル一覧: {", ".join(EXPRESSION_LABELS)}"""

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

あわせて、あなたの今の表情を次のルールで1つだけ選んでください。

{EXPRESSION_GUIDE}

必ず次のJSON形式のみで出力してください（説明文や余計なテキストを含めないこと）:
{{"text": "発言内容", "expression": "ラベル名"}}
"""

# ── オープンラボ用 ──
# 本番の質問（QUESTION_SETS / RESERVE_QUESTIONS）とは話題が重ならないものにする。
# 本番の質問を事前に知った人は回答を準備でき、評価不安や自己開示の測定に影響するため。
DEMO_QUESTIONS = [
    "最近ハマっていることを教えてください。",
    "休日はどんなふうに過ごすことが多いですか。",
    "今いちばん行ってみたい場所はどこですか。",
]
# 評価・採点・合否には触れない（本番の締めは「評価されます」と告げるが、デモでは見せない）
DEMO_CLOSING_TEXT = "ありがとうございました。デモはここまでです。体験していただきありがとうございました。"

DEMO_SYSTEM_PROMPT = f"""あなたは、大学の研究室公開（オープンラボ）で来場者と話す、アバターの面接官です。
これは面接練習システムの体験デモです。来場者に、アバターと声で会話する体験を気軽に楽しんでもらうことが目的です。
自然な日本語の話し言葉で、来場者の話に相づちを打ち、次の軽い質問を1つだけ返してください。
返答はTTSで読み上げられるため、1〜2文程度で簡潔にしてください。

【話題】
- 次のような、気軽に答えられる身近な話題だけを扱ってください:
  最近ハマっていること / 休日の過ごし方 / 行ってみたい場所 / 好きな食べ物 / 最近うれしかったこと
- 就職活動・志望動機・自己PR・長所短所・学生時代に力を入れたこと・チームでの経験・困難の乗り越え方など、
  本格的な面接の質問はしないでください。

【してはいけないこと】
- 来場者の回答を評価・採点したり、良し悪しや合否について述べたりしないでください。
- 研究の目的・実験の内容・評価の方法・誰が評価するのか・本番の質問・アンケートなどについて聞かれたら、
  肯定も否定もせず、相手の質問の言葉（「評価」「実験」「本番」など）も繰り返さずに、
  「そのあたりは研究室のメンバーに聞いてみてください」とだけ言って、身近な話題の質問に戻ってください。
- あなたが誰に操作されているか、AI か人かについても、同じように答えないでください。

あわせて、あなたの今の表情を次のルールで1つだけ選んでください。

{EXPRESSION_GUIDE}

必ず次のJSON形式のみで出力してください（説明文や余計なテキストを含めないこと）:
{{"text": "発言内容", "expression": "ラベル名"}}
"""

# 応答生成に使うプロンプト（モードで切り替える）
ACTIVE_SYSTEM_PROMPT = DEMO_SYSTEM_PROMPT if OPEN_LAB_MODE else SYSTEM_PROMPT

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

for _fixed in (OPENING_EXPRESSION, SEED_EXPRESSION, CLOSING_EXPRESSION, DEFAULT_EXPRESSION):
    assert _fixed in EXPRESSION_LABELS, f"固定の表情 {_fixed} が許可一覧にありません"

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
            interaction = call_with_deadline(
                chat_client.interactions.create,
                CHAT_TIMEOUT_SEC,
                model=model,
                system_instruction=ACTIVE_SYSTEM_PROMPT,
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
        if expression:
            logger.warning("CHAT returned disallowed expression %r -> %s", expression, DEFAULT_EXPRESSION)
        expression = DEFAULT_EXPRESSION
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


REACTION_SYSTEM_PROMPT = f"""あなたは就職面接の面接官です。
これから次の質問に移りますが、その前に、参加者の直前の回答を受け止めるあなたの表情を1つだけ選んでください。
発言は不要です。

{EXPRESSION_GUIDE}

必ず次のJSON形式のみで出力してください:
{{"expression": "ラベル名"}}
"""

REACTION_SCHEMA = {
    "type": "object",
    "properties": {"expression": {"type": "string", "enum": EXPRESSION_LABELS}},
    "required": ["expression"],
    "additionalProperties": False,
}
# シード質問へ移るターンは本来 LLM を通さず即答できるため、表情判定で待たせすぎないよう短めにする
REACTION_TIMEOUT_SEC = float(os.environ.get("GEMINI_REACTION_TIMEOUT_SEC", "4"))


def react_expression(history: list[dict], default: str) -> tuple[str, float]:
    """直前の回答に対する表情だけを判定する（シード質問へ移るターン用）。
    失敗・タイムアウト時は default を返す（質問文には影響しない）。"""
    t0 = time.perf_counter()
    try:
        interaction = call_with_deadline(
            chat_client.interactions.create,
            REACTION_TIMEOUT_SEC,
            model=CHAT_MODEL,
            system_instruction=REACTION_SYSTEM_PROMPT,
            input=format_interview_history(history),
            response_format={"type": "text", "mime_type": "application/json", "schema": REACTION_SCHEMA},
            generation_config={"thinking_level": CHAT_THINKING_LEVEL},
            store=False,
            timeout=REACTION_TIMEOUT_SEC,
        )
        expression = str(json.loads(interaction.output_text or "{}").get("expression", "")).strip()
        if expression in EXPRESSION_LABELS:
            return expression, time.perf_counter() - t0
    except Exception as e:
        logger.warning("Reaction expression failed (%s) -> %s", type(e).__name__, default)
    return default, time.perf_counter() - t0


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
    if OPEN_LAB_MODE:
        # 本番の質問セット・7分の構成は見せない。回の指定は無視してデモ用に固定する
        qs = 0
        main_seeds = list(DEMO_QUESTIONS)
        reserve = []
        duration = OPEN_LAB_DURATION_SEC
    else:
        qs = req.question_set if req.question_set in QUESTION_SETS else 1
        main_seeds = list(QUESTION_SETS[qs])
        reserve = list(RESERVE_QUESTIONS.get(qs, []))
        duration = max(60.0, float(req.duration_seconds))
    SESSION.clear()
    INTERVIEW.update(
        active=True,
        finished=False,
        question_set=qs,
        questions=main_seeds + reserve,
        n_main=len(main_seeds),
        seed_index=0,
        probes=0,
        start_time=time.monotonic(),
        duration=duration,
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
            r = await asyncio.to_thread(generate_reply, list(SESSION), PROBE_INSTRUCTION)
        except Exception as e:
            if SESSION and SESSION[-1]["role"] == "user":
                SESSION.pop()
            logger.error("Probe failed (user=%r)", _redact(message))
            raise_gemini_error("追質問", e)
        INTERVIEW["probes"] += 1
        text, expression, raw, model = r["text"], r["expression"], r["raw"], r["model"]
        in_tok, out_tok, tot_tok, dt = r["in"], r["out"], r["tot"], r["dt"]
        phase, is_final = "probe", False

    elif decision == "seed":
        INTERVIEW["seed_index"] += 1
        INTERVIEW["probes"] = 0
        text = SEED_CONNECTOR + INTERVIEW["questions"][INTERVIEW["seed_index"]]
        # 質問文は逐語のまま。表情だけ直前の回答への反応として判定する
        expression, dt = await asyncio.to_thread(react_expression, list(SESSION), SEED_EXPRESSION)
        raw, model = "", "(fixed text)"
        in_tok = out_tok = tot_tok = None
        phase, is_final = "seed", False

    else:  # closing
        text = DEMO_CLOSING_TEXT if OPEN_LAB_MODE else CLOSING_TEXT
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
        INTERVIEW["probes"], rem, _redact(message), expression, _redact(text),
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


OPEN_LAB_FREE = {"start": 0.0}  # オープンラボの自由対話で、今のデモが始まった時刻


@app.post("/api/interview")
async def interview(req: ApiRequest):
    # ── 準構造化モード（/api/interview/start 済み）──────────────
    if INTERVIEW["active"] and not INTERVIEW["finished"] and req.message is not None:
        return await structured_turn(req.message)

    # ── オープンラボ: 自由対話も1回あたりの時間で区切る（次の来場者へ交代しやすくする）──
    if OPEN_LAB_MODE and req.message is not None:
        now = time.monotonic()
        if not OPEN_LAB_FREE["start"] or not SESSION:
            OPEN_LAB_FREE["start"] = now
        elif now - OPEN_LAB_FREE["start"] >= OPEN_LAB_DURATION_SEC:
            # 締めて終了。次の発話からは新しいデモとして始める
            SESSION.clear()
            OPEN_LAB_FREE["start"] = 0.0
            logger.info("OPEN LAB demo finished (free mode, %.0fs)", OPEN_LAB_DURATION_SEC)
            log_conversation({"endpoint": "interview", "mode": "free", "phase": "closing", "expression": CLOSING_EXPRESSION})
            return {
                "model": "(fixed)", "expression": CLOSING_EXPRESSION, "text": DEMO_CLOSING_TEXT, "raw": "",
                "phase": "closing", "is_final": True, "seed_index": -1, "question_set": 0, "remaining_seconds": 0.0,
            }

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
        r = await asyncio.to_thread(generate_reply, history)
    except Exception as e:
        # 失敗した場合はさっき積んだユーザー発話を巻き戻す（次回の文脈汚染を防ぐ）
        if server_memory and SESSION and SESSION[-1]["role"] == "user":
            SESSION.pop()
        logger.error("Interview failed (user=%r)", _redact(last_user))
        raise_gemini_error("面接応答", e)

    # 応答をサーバー側メモリに追記（新方式のみ）
    if server_memory:
        SESSION.append({"role": "assistant", "content": r["text"]})

    logger.info(
        "CHAT model=%s memory=%s turns=%d tokens(p/c/t)=%s/%s/%s %.2fs  user=%r -> [%s] %r",
        r["model"], server_memory, len(history),
        r["in"], r["out"], r["tot"],
        r["dt"], _redact(last_user), r["expression"], _redact(r["text"]),
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
            interaction = await asyncio.to_thread(
                call_with_deadline,
                chat_client.interactions.create,
                TTS_TIMEOUT_SEC,
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
        model, TTS_VOICE, len(req.text), len(audio), dt, _redact(req.text),
    )
    log_conversation({
        "endpoint": "tts", "model": model, "voice": TTS_VOICE,
        "chars": len(req.text), "audio_bytes": len(audio),
        "latency_sec": round(dt, 2), "text": req.text,
    })

    return Response(content=audio, media_type="audio/wav")
