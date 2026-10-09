import argparse
import sys
import os
from pathlib import Path
from ocr_converter import load_images_from_paths, convert_images_to_word_html

def main():
    parser = argparse.ArgumentParser(
        description="Chuyển đổi hình ảnh / PDF đề thi sang Microsoft Word HTML chuẩn in ấn và MathML."
    )
    parser.add_argument("inputs", nargs="+", help="Đường dẫn đến các file ảnh hoặc PDF cần xử lý")
    parser.add_argument("-o", "--output", default="output.doc", help="File đầu ra (.doc hoặc .html)")
    parser.add_argument("-m", "--model", default="gemini-3.1-flash-lite", help="Mô hình Gemini (mặc định: gemini-3.1-flash-lite)")
    parser.add_argument("-k", "--key", default=None, help="Gemini API Key (hoặc thiết lập biến môi trường GEMINI_API_KEY)")

    args = parser.parse_args()

    api_key = args.key or os.environ.get("GEMINI_API_KEY")
    if not api_key:
        cfg = Path.home() / ".ocr_word_gemini_key.txt"
        if cfg.exists():
            api_key = cfg.read_text(encoding="utf-8").strip()

    if not api_key:
        print("Lỗi: Không tìm thấy Gemini API Key. Hãy truyền qua -k/--key hoặc biến môi trường GEMINI_API_KEY.")
        sys.exit(1)

    print(f"[*] Đang nạp danh sách file: {args.inputs}")
    images = load_images_from_paths(args.inputs)
    if not images:
        print("Lỗi: Không tìm thấy ảnh hoặc trang tài liệu hợp lệ nào.")
        sys.exit(1)

    print(f"[*] Đã nạp {len(images)} trang ảnh.")
    print(f"[*] Đang gửi dữ liệu đến Gemini ({args.model})...")

    try:
        html_code = convert_images_to_word_html(
            api_key=api_key,
            images=images,
            model_name=args.model,
            progress_callback=lambda msg: print(f"    -> {msg}")
        )
        with open(args.output, "w", encoding="utf-8") as f:
            f.write(html_code)
        print(f"[✓] Đã tạo thành công file: {args.output}")
        print("[✓] Bạn có thể mở trực tiếp bằng Microsoft Word!")
    except Exception as e:
        print(f"[!] Thất bại: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()
