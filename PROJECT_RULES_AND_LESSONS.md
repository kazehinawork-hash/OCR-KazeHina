# SYSTEM RULES & LESSONS LEARNED - DỰ ÁN OCR KAZEHINA (OCR PDF / ẢNH SANG WORD)

Tài liệu này là cẩm nang hướng dẫn dành cho **các AI Developer kế nhiệm** khi tiếp nhận, bảo trì, hoặc phát triển thêm tính năng cho dự án **OCR KazeHina**. Mọi AI khi làm việc trên dự án này **BẮT BUỘC** phải đọc kỹ các quy chuẩn kỹ thuật và bài học xương máu dưới đây.

---

## 1. TỔNG QUAN KIẾN TRÚC DỰ ÁN

- **Mục tiêu**: Bóc tách OCR hình ảnh tài liệu, bài tập, đề thi học tập (Toán, Vật Lý, Hóa Học) từ ảnh/PDF/Clipboard sang tập tin **HTML tương thích 100% với Microsoft Word** (lưu dưới đuôi `.doc`).
- **Nền tảng chính (Desktop App)**:
  - Dự án C# WPF .NET 10.0: nằm trong thư mục `OcrWordApp/`.
  - Giao diện Liquid Glass 3.0 dựa trên WPF-UI (`Themes/LiquidGlass.xaml` và `Themes/AppStyles.xaml`).
  - Bản xuất bản phân phối:
    - Thư mục thông thường: `AppPublish/OcrWordApp.exe`.
    - Bản Portable độc lập (Self-Contained Single-File): `OCR_KazeHina_Portable/OCR_KazeHina.exe` (không cần cài bất cứ gì, chạy ngay trên máy khác).
- **Module phụ (Python CLI / Fallback)**:
  - `prompt_template.py`, `ocr_converter.py`, `cli.py`, `app_gui.py`.

---

## 2. QUY TẮC CỐT LÕI VỀ ĐỊNH DẠNG WORD HTML (BẮT BUỘC TUÂN THỦ)

### Quy tắc 1: Công Thức Toán - Lý - Hóa Phải 100% MathML
- **Tại sao?**: Khi Microsoft Word mở file HTML chứa MathML `<math xmlns="http://www.w3.org/1998/Math/MathML">`, Word sẽ tự động chuyển đổi thành **ô Equation nguyên bản (Word Native Equation Box)**. Người dùng có thể chỉnh sửa phân số, số mũ, căn thức như gõ công thức tay trong Word.
- **Cấm**: Tuyệt đối không dùng LaTeX thuần (`$...$`, `\frac`), không dùng ảnh chụp công thức, không để text thường.
- **Vectơ**: `<math xmlns="http://www.w3.org/1998/Math/MathML"><mover><mi>B</mi><mo>&rarr;</mo></mover></math>`.
- **Căn thức**: `<math xmlns="http://www.w3.org/1998/Math/MathML"><msqrt><mn>3</mn></msqrt></math>`.
- **Phân số**: `<math xmlns="http://www.w3.org/1998/Math/MathML"><mfrac><mi>&pi;</mi><mn>2</mn></mfrac></math>`.

### Quy tắc 2: Định Dạng Đáp Án Trắc Nghiệm A, B, C, D (Bài Học Rút Ra Về Lỗi Nhảy Dòng)
- **Bài học xương máu**: Trước đây dùng `tab-stops: 4.25cm 8.5cm 12.75cm;` với phím Tab `&#9;`. Tuy nhiên khi các phương án chứa công thức MathML dài, Word bị tràn lề phải khiến **đáp án D bị rớt xuống dòng thứ 2**, hiển thị rất xấu.
- **Giải pháp chuẩn**:
  - Dùng **Bảng bố cục ẩn viền hoàn toàn** (`border: none; border-collapse: collapse;`):
    - 4 đáp án trên 1 dòng: Bảng 1 hàng, 4 cột `width: 25%`.
    - 2 đáp án trên 1 dòng: Bảng 2 hàng, mỗi hàng 2 cột `width: 50%`.
  - Trong `GeminiOcrService.cs`, đã có hàm Regex tự động convert bất kỳ dòng tab-stop nào thành bảng ẩn viền để bảo đảm an toàn kép.
- **Màu sắc đáp án**: Chữ cái **A.**, **B.**, **C.**, **D.** mang màu xanh `#1a56db` giống như các tài liệu đề thi in ấn.

### Quy tắc 3: Chống Kéo Dãn Chữ (Justify Bug trong Word)
- **Cấm**: Tuyệt đối KHÔNG gán `style="text-align: justify;"` ở thẻ `<body>`, `<td>` hoặc `<div class="Section1">`. Word sẽ kéo toạc các dòng ngắn và dấu gạch đầu dòng.
- Chỉ gán `justify` cho thẻ `<p>` chứa đoạn văn xuôi dài từ 2 dòng trở lên.
- Các gạch đầu dòng (`-`) phải là các thẻ `<p>` độc lập với `margin: 2pt 0 2pt 18pt; text-align: left;`.
- Giữa số lượng và đơn vị đo bắt buộc dùng khoảng trắng cứng `&nbsp;` (ví dụ `10&nbsp;cm`, `0,05&nbsp;T`, `15&nbsp;Wb`).

### Quy tắc 4: Mở File Không Hỏi Bảng Mã (Encoding)
- File khi lưu ra đĩa BẮT BUỘC dùng **UTF-8 có BOM (Byte Order Mark)**:
  `File.WriteAllTextAsync(outputPath, htmlOutput, new System.Text.UTF8Encoding(true));`
- Trong `<head>` bắt buộc có:
  `<meta http-equiv="Content-Type" content="text/html; charset=utf-8">`
  `<meta charset="utf-8">`

---

## 3. BÀI HỌC VỀ GEMINI API & DANH SÁCH MODEL

### Bài học 1: Các Model Đã Bị Khai Tử (HTTP 404)
- Google đã chính thức chặn các model cũ đối với API request mới:
  - `gemini-1.5-flash`, `gemini-1.5-pro` (Đã chết / 404)
  - `gemini-2.5-flash`, `gemini-2.5-pro` (Đã chết / 404)
- Model `gemini-3.8-flash` hay bị quá tải tạm thời (503).
- **Các model đang hoạt động tốt nhất**:
  - **`gemini-3.1-flash-lite`** (Được chọn làm **MẶC ĐỊNH SỐ 1** của dự án): Nhẹ, phản hồi tức thì, bóc tách chính xác.
  - **`gemini-3.6-flash`**: Siêu nhanh và thông minh.
  - **`gemini-3.5-flash-lite`** và **`gemini-3.7-flash`**.

### Bài học 2: Lọc Danh Sách Model Quét Về
- API trả về rất nhiều model chuyên biệt không dùng được cho Vision OCR (ví dụ `tts`, `robot`, `embedding`, `banana`...).
- Hàm `GetAvailableModelsAsync` trong `GeminiOcrService.cs` đã được viết bộ lọc: chỉ giữ lại 4–5 model tốt nhất để người dùng không bị rối hoặc bấm nhầm model hỏng.

---

## 4. BÀI HỌC VỀ CLIPBOARD & LỖI ẢNH ĐEN XÌ CỦA ZALO

### Bài học: Zalo Screenshot Bug (Alpha = 0)
- **Triệu chứng**: Chụp màn hình bằng Zalo rồi bấm Ctrl+V vào app thì ảnh bị biến thành màu đen kịt.
- **Nguyên nhân**: Zalo đưa ảnh vào Clipboard dưới dạng **DIB 32-bit**, nhưng set toàn bộ kênh Alpha (độ trong suốt) = 0. Khi WPF đọc ảnh có Alpha = 0, nó coi ảnh là trong suốt hoàn toàn, khi vẽ ra nền tối sẽ thành đen sì.
- **Giải pháp**:
  - Xem cách xử lý trong `MainWindow.xaml.cs` (hàm `PasteFromClipboard()`):
    1. Ưu tiên đọc stream `PNG` trực tiếp từ Clipboard nếu có.
    2. Nếu đọc DIB: tái tạo header BMP và ép chuyển đổi sang `PixelFormats.Bgr32` (Opaque) để loại bỏ hoàn toàn kênh Alpha.
    3. Lưu ảnh thành PNG chuẩn và add vào danh sách.

---

## 5. BÀI HỌC VỀ ĐÓNG GÓI PORTABLE (KHÔNG CẦN CÀI ĐẶT)

Khi đóng gói để người dùng chạy trên máy khác không có .NET:
- Lệnh dotnet publish chuẩn:
  ```powershell
  dotnet publish OcrWordApp/OcrWordApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o OCR_KazeHina_Portable
  ```
- File đầu ra: Duy nhất `OCR_KazeHina_Portable/OCR_KazeHina.exe`. Xóa file `OcrWordApp.exe` trùng lặp và các file `.pdb`.
- File hướng dẫn người dùng non-tech kèm ảnh minh họa: Duy nhất `OCR_KazeHina_Portable/Huong_Dan_Su_Dung.doc` (không để file doc hướng dẫn ở thư mục gốc).
- **YÊU CẦU CỦA USER**: TUYỆT ĐỐI KHÔNG TẠO FILE NÉN `.zip`, chỉ cần cập nhật trực tiếp thư mục `OCR_KazeHina_Portable/`.
- **YÊU CẦU GIT/GITHUB**: TUYỆT ĐỐI CHỈ COMMIT VÀ PUSH LÊN GITHUB KHI USER YÊU CẦU CỤ THỂ. Không tự ý push trong quá trình phát triển thường ngày.
- Tuyệt đối không bật `<UseWindowsForms>true</UseWindowsForms>` nếu không cần thiết để tránh xung đột namespace với WPF.

---

## 6. HƯỚNG DẪN DÀNH CHO AI TIẾP QUẢN
1. **Trước khi sửa code**: Đọc file [PromptConstants.cs](file:///E:/OneDrive/3.家%20Jiā%20Home/99.%20其他%20Qítā%20Other/95.Ocr%20Pdf/OcrWordApp/PromptConstants.cs) để nắm vững prompt chuẩn của hệ thống.
2. **Khi thay đổi giao diện**: Tuân thủ theme Liquid Glass 3.0 (`InteractiveButtonNeon`, `GlassGradientBorderBrush`).
3. **Khi build xong**: Luôn test bằng `dotnet build` trước, sau đó publish vào cả `AppPublish` và `OCR_KazeHina_Portable`.
