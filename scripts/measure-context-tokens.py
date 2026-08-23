"""Count tokens in raw and compact smoke-test transcripts using tiktoken."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compare exact tokenizer counts for two context transcript files."
    )
    parser.add_argument("raw", type=Path, help="Path to raw-context.txt")
    parser.add_argument("compact", type=Path, help="Path to compact-context.txt")
    parser.add_argument(
        "--encoding",
        default="o200k_base",
        help="tiktoken encoding name (default: o200k_base)",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        import tiktoken
    except ImportError as error:
        raise SystemExit(
            "tiktoken is not installed. Install it in an isolated Python environment "
            "with: python -m pip install tiktoken"
        ) from error

    encoding = tiktoken.get_encoding(args.encoding)
    raw_text = args.raw.read_text(encoding="utf-8")
    compact_text = args.compact.read_text(encoding="utf-8")
    raw_tokens = len(encoding.encode(raw_text))
    compact_tokens = len(encoding.encode(compact_text))
    reduction = 0.0 if raw_tokens == 0 else (1.0 - compact_tokens / raw_tokens) * 100.0

    print(
        json.dumps(
            {
                "encoding": args.encoding,
                "rawTokens": raw_tokens,
                "compactTokens": compact_tokens,
                "reductionPercent": round(reduction, 2),
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
