import argparse
import os
import sys
from pathlib import Path
from typing import Callable, Optional

from google import genai
from google.genai import types

from ocr_converter import clean_html_output
from prompt_template import SYSTEM_PROMPT


def reconstruct_transcript_to_word_html(
    api_key: str,
    transcript_text: str,
    model_name: str = "gemini-3.1-flash-lite",
    progress_callback: Optional[Callable[[str], None]] = None,
) -> str:
    """Tái tạo transcript văn bản tự do thành HTML tương thích Microsoft Word."""
    text = (transcript_text or "").strip()
    if not text:
        raise ValueError("Transcript rỗng, không có nội dung để tái tạo.")

    if progress_callback:
        progress_callback(f"Đang gửi transcript đến Gemini ({model_name})...")

    client = genai.Client(api_key=api_key)

    contents = [
        SYSTEM_PROMPT,
        "\n\nDưới đây là transcript văn bản tự do của tài liệu cần tái tạo thành "
        "HTML Word. Hãy nhận diện câu hỏi, đáp án A/B/C/D và công thức để dựng "
        "đúng định dạng MathML và bố cục chuẩn in ấn:\n\n" + text,
    ]

    response = client.models.generate_content(
        model=model_name,
        contents=contents,
        config=types.GenerateContentConfig(
            temperature=0.1,
        ),
    )

    return clean_html_output(response.text)


def _resolve_api_key(cli_key: Optional[str]) -> Optional[str]:
    if cli_key:
        return cli_key
    env_key = os.environ.get("GEMINI_API_KEY")
    if env_key:
        return env_key
    cfg = Path.home() / ".ocr_word_gemini_key.txt"
    if cfg.exists():
        return cfg.read_text(encoding="utf-8").strip()
    return None


def main():
    parser = argparse.ArgumentParser(
        description="Tái tạo transcript văn bản tự do thành HTML Word chuẩn in ấn và MathML."
    )
    parser.add_argument("transcript", nargs="?", help="File transcript (.txt). Bỏ trống để đọc từ stdin.")
    parser.add_argument("-o", "--output", default="output.doc", help="File đầu ra (.doc hoặc .html)")
    parser.add_argument("-m", "--model", default="gemini-3.1-flash-lite", help="Mô hình Gemini (mặc định: gemini-3.1-flash-lite)")
    parser.add_argument("-k", "--key", default=None, help="Gemini API Key (hoặc thiết lập biến môi trường GEMINI_API_KEY)")

    args = parser.parse_args()

    api_key = _resolve_api_key(args.key)
    if not api_key:
        print("Lỗi: Không tìm thấy Gemini API Key. Hãy truyền qua -k/--key hoặc biến môi trường GEMINI_API_KEY.")
        sys.exit(1)

    if args.transcript:
        try:
            transcript_text = Path(args.transcript).read_text(encoding="utf-8")
        except OSError as e:
            print(f"Lỗi: Không đọc được file transcript: {e}")
            sys.exit(1)
    else:
        transcript_text = sys.stdin.read()

    try:
        html_code = reconstruct_transcript_to_word_html(
            api_key=api_key,
            transcript_text=transcript_text,
            model_name=args.model,
            progress_callback=lambda msg: print(f"    -> {msg}"),
        )
        with open(args.output, "w", encoding="utf-8-sig") as f:
            f.write(html_code)
        print(f"[✓] Đã tạo thành công file: {args.output}")
        print("[✓] Bạn có thể mở trực tiếp bằng Microsoft Word!")
    except Exception as e:
        print(f"[!] Thất bại: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
