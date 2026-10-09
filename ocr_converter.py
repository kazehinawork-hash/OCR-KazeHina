import re
from pathlib import Path
from typing import List, Callable, Optional
from google import genai
from google.genai import types
from PIL import Image
import fitz  # PyMuPDF
from prompt_template import SYSTEM_PROMPT


def pdf_to_images(pdf_path: str, dpi: int = 200) -> List[Image.Image]:
    """Chuyển đổi các trang trong file PDF thành danh sách ảnh PIL."""
    doc = fitz.open(pdf_path)
    images = []
    zoom = dpi / 72.0
    matrix = fitz.Matrix(zoom, zoom)
    for page_idx in range(len(doc)):
        page = doc.load_page(page_idx)
        pix = page.get_pixmap(matrix=matrix)
        img = Image.frombytes("RGB", [pix.width, pix.height], pix.samples)
        images.append(img)
    return images


def load_images_from_paths(file_paths: List[str]) -> List[Image.Image]:
    """Tải danh sách ảnh hoặc tách trang PDF ra ảnh PIL."""
    all_images = []
    for path_str in file_paths:
        p = Path(path_str)
        if not p.exists():
            continue
        if p.suffix.lower() == ".pdf":
            all_images.extend(pdf_to_images(str(p)))
        elif p.suffix.lower() in [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff"]:
            img = Image.open(str(p)).convert("RGB")
            all_images.append(img)
    return all_images


def clean_html_output(raw_text: str) -> str:
    """Loại bỏ markdown code block và làm sạch văn bản."""
    text = raw_text.strip()
    # Loại bỏ citation [1], [2], [12], ...
    text = re.sub(r'\[\d+\]', '', text)
    
    # Loại bỏ ```html và ``` nếu mô hình có lỡ bao bọc
    if text.startswith("```html"):
        text = text[7:]
    elif text.startswith("```"):
        text = text[3:]
    if text.endswith("```"):
        text = text[:-3]
        
    text = text.strip()
    return text


def convert_images_to_word_html(
    api_key: str,
    images: List[Image.Image],
    model_name: str = "gemini-3.1-flash-lite",
    progress_callback: Optional[Callable[[str], None]] = None,
) -> str:
    """Gửi các ảnh và system prompt tới Gemini để sinh ra file HTML chuẩn Word."""
    if not images:
        raise ValueError("Không có hình ảnh nào để xử lý.")
        
    if progress_callback:
        progress_callback(f"Đang chuẩn bị gửi {len(images)} ảnh đến Gemini ({model_name})...")
        
    client = genai.Client(api_key=api_key)
    
    contents = [
        SYSTEM_PROMPT,
        "\n\nDưới đây là toàn bộ hình ảnh tài liệu cần số hóa chuẩn Word HTML:"
    ]
    for idx, img in enumerate(images, 1):
        contents.append(f"\n--- ẢNH TRANG {idx} ---")
        contents.append(img)
        
    if progress_callback:
        progress_callback("Đang gửi yêu cầu và đợi AI xử lý bóc tách OCR...")
        
    response = client.models.generate_content(
        model=model_name,
        contents=contents,
        config=types.GenerateContentConfig(
            temperature=0.1,  # Giữ nhiệt độ thấp để trích xuất chuẩn xác
        )
    )
    
    html_content = clean_html_output(response.text)
    return html_content
