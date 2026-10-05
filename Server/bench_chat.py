"""CHAT（面接応答生成）のレイテンシを単体で計測するスクリプト。

使い方（Server フォルダで実行）:
    python bench_chat.py                                   # 既定モデルで thinking を比較
    python bench_chat.py --models gemini-3.7-flash other-model --levels minimal low --runs 3

サーバー本体と同じ SYSTEM_PROMPT / 応答スキーマ / 入力形式でリクエストし、
モデル × thinking_level ごとに所要時間とトークン数を表示する。
"""
import argparse
import logging
import statistics
import time

import main  # .env 読み込み・client・プロンプト定義を流用する

# SDK のリトライ（429/503 など）が起きたら表示されるようにする
logging.basicConfig(level=logging.INFO, format="    [sdk] %(message)s")
logging.getLogger("avatar_interview").setLevel(logging.WARNING)

SAMPLE_HISTORY = [{"role": "user", "content": "よろしくお願いします。"}]


def run_once(model: str, level: str) -> dict:
    t0 = time.perf_counter()
    interaction = main.client.interactions.create(
        model=model,
        system_instruction=main.SYSTEM_PROMPT,
        input=main.format_interview_history(SAMPLE_HISTORY),
        response_format={
            "type": "text",
            "mime_type": "application/json",
            "schema": main.INTERVIEW_RESPONSE_SCHEMA,
        },
        generation_config={"thinking_level": level},
        store=False,
    )
    dt = time.perf_counter() - t0
    usage = getattr(interaction, "usage", None)
    return {
        "dt": dt,
        "in": getattr(usage, "total_input_tokens", None),
        "out": getattr(usage, "total_output_tokens", None),
        "tot": getattr(usage, "total_tokens", None),
        "text": (interaction.output_text or "")[:60],
    }


def main_cli() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--models", nargs="+", default=[main.CHAT_MODEL])
    p.add_argument("--levels", nargs="+", default=["low", "medium", "high"])
    p.add_argument("--runs", type=int, default=3)
    args = p.parse_args()

    summary = []
    for model in args.models:
        for level in args.levels:
            print(f"\n=== model={model}  thinking={level} ===")
            times = []
            for i in range(args.runs):
                try:
                    r = run_once(model, level)
                except Exception as e:
                    print(f"  run{i + 1}: ERROR {e}")
                    continue
                times.append(r["dt"])
                print(
                    f"  run{i + 1}: {r['dt']:6.2f}s  tokens(p/c/t)={r['in']}/{r['out']}/{r['tot']}"
                    f"  {r['text']!r}"
                )
            if times:
                summary.append((model, level, statistics.median(times), min(times), max(times)))

    print("\n=== summary (sec) ===")
    print(f"{'model':<36} {'thinking':<9} {'median':>7} {'min':>7} {'max':>7}")
    for model, level, med, lo, hi in summary:
        print(f"{model:<36} {level:<9} {med:7.2f} {lo:7.2f} {hi:7.2f}")


if __name__ == "__main__":
    main_cli()
