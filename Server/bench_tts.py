"""TTS のレイテンシを単体で計測し、聞き比べ用の WAV を保存するスクリプト。

使い方（Server フォルダで実行）:
    python bench_tts.py
    python bench_tts.py --models gemini-3.8-flash-lite-tts gemini-3.1-flash-tts-preview --runs 3

音声は logs/tts_bench/<model>_<文字数>chars_<run>.wav に保存される。
"""
import argparse
import base64
import logging
import statistics
import sys
import time

import main  # .env 読み込み・client・pcm_to_wav を流用する

logging.basicConfig(level=logging.INFO, format="    [sdk] %(message)s")
logging.getLogger("avatar_interview").setLevel(logging.WARNING)
logging.getLogger("httpx").setLevel(logging.WARNING)

SAMPLE_TEXT = "よろしくお願いします。それではまず、簡単に自己紹介をお願いできますでしょうか。"
DEFAULT_MODELS = [
    "gemini-3.1-flash-tts-preview",
    "gemini-3.8-flash-tts",
    "gemini-3.8-flash-lite-tts",
]
OUT_DIR = main.LOG_DIR / "tts_bench"


def run_once(model: str, text: str, voice: str) -> tuple[float, bytes]:
    t0 = time.perf_counter()
    interaction = main.chat_client.interactions.create(
        model=model,
        input=text,
        response_format={"type": "audio"},
        generation_config={"speech_config": [{"voice": voice}]},
        store=False,
        timeout=30,
    )
    dt = time.perf_counter() - t0
    data = interaction.output_audio.data
    pcm = base64.b64decode(data) if isinstance(data, str) else bytes(data)
    return dt, main.pcm_to_wav(pcm)


def main_cli() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    p = argparse.ArgumentParser()
    p.add_argument("--models", nargs="+", default=DEFAULT_MODELS)
    p.add_argument("--voice", default=main.TTS_VOICE)
    p.add_argument("--text", default=SAMPLE_TEXT)
    p.add_argument("--runs", type=int, default=2)
    args = p.parse_args()
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    summary = []
    for model in args.models:
        print(f"\n=== model={model}  voice={args.voice} ===")
        times = []
        for i in range(args.runs):
            try:
                dt, wav = run_once(model, args.text, args.voice)
            except Exception as e:
                print(f"  run{i + 1}: ERROR {str(e)[:160]}")
                continue
            times.append(dt)
            # 16bit mono 24kHz 前提で再生時間を概算
            audio_sec = (len(wav) - 44) / (24000 * 2)
            path = OUT_DIR / f"{model}_{len(args.text)}chars_{i + 1}.wav"
            path.write_bytes(wav)
            print(f"  run{i + 1}: {dt:6.2f}s  audio={audio_sec:4.1f}s  -> {path.name}")
        if times:
            summary.append((model, statistics.median(times), min(times), max(times)))

    print(f"\n=== summary (sec) ===  text={len(args.text)} chars")
    print(f"{'model':<34} {'median':>7} {'min':>7} {'max':>7}")
    for model, med, lo, hi in summary:
        print(f"{model:<34} {med:7.2f} {lo:7.2f} {hi:7.2f}")
    print(f"\nWAV: {OUT_DIR}")


if __name__ == "__main__":
    main_cli()
