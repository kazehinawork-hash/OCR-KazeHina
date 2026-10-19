# SYSTEM RULES & LESSONS LEARNED - DỰ ÁN OCR KAZEHINA

Tài liệu này là cẩm nang tổng hợp các **Quy chuẩn kỹ thuật (Rules)** và **Bài học kinh nghiệm thực chiến (Lessons Learned)** khi phát triển, gỡ lỗi và nâng cấp ứng dụng **OCR KazeHina**. Mọi AI Assistant và lập trình viên khi tiếp nhận dự án **BẮT BUỘC** phải tuân thủ nghiêm ngặt các nguyên tắc dưới đây.

---

## 1. TỔNG QUAN KIẾN TRÚC DỰ ÁN

- **Mục tiêu**: Bóc tách hình ảnh tài liệu, bài tập, đề thi học tập (Toán, Vật Lý, Hóa Học) từ Ảnh chụp / PDF / Clipboard sang file **Microsoft Word HTML (`.doc`)**, giữ chuẩn ô công thức **Word Native Equation**.
- **Cấu trúc Source Code**:
  - `OcrWordApp/`: Ứng dụng Desktop chính viết bằng **C# WPF .NET 10.0** theo phong cách **Liquid Glass Fluent 3.0** (`Themes/LiquidGlass.xaml`).
  - Thư mục xuất bản ứng dụng: [AppPublish/](file:///E:/OneDrive/3.家%20Jiā%20Home/99.%20其他%20Qítā%20Other/95.Ocr%20Pdf/AppPublish) chứa duy nhất file thực thi độc lập [OCR_KazeHina.exe](file:///E:/OneDrive/3.家%20Jiā%20Home/99.%20其他%20Qítā%20Other/95.Ocr%20Pdf/AppPublish/OCR_KazeHina.exe) (~67.6 MB, tự tích hợp Runtime).
  - File chạy ứng dụng một chạm: [Chay_App_CSharp.bat](file:///E:/OneDrive/3.家%20Jiā%20Home/99.%20其他%20Qítā%20Other/95.Ocr%20Pdf/Chay_App_CSharp.bat).

---

## 2. QUY TẮC CỐT LÕI (CORE RULES)

### 📌 Rule 1: Quy tắc Git & GitHub
- **TUYỆT ĐỐI CHỈ COMMIT VÀ PUSH LÊN GITHUB KHI USER YÊU CẦU CỤ THỂ**.
- Tuyệt đối không tự ý chạy `git push` sau mỗi lần sửa code hay test tính năng.
- Khi tạo GitHub Release: Định dạng tiêu đề thống nhất dạng `OCR KazeHina vX.Y.Z` (ví dụ `OCR KazeHina v1.1.0`), file đính kèm chính phải là `OCR_KazeHina.exe` độc lập.

### 📌 Rule 2: Quy chuẩn Đóng gói & Phát hành (.NET Single-File)
- Dự án xuất bản thành duy nhất **1 file EXE độc lập (Self-Contained Single-File)** có nén để người dùng cắm USB hoặc gửi sang máy khác không cần cài đặt .NET:
  ```powershell
  dotnet publish OcrWordApp/OcrWordApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o AppPublish
  ```
- File `.pdb` (Program Database) chỉ dùng để debug nội bộ, khi đóng gói ra `AppPublish/` hoặc Release bắt buộc **xóa bỏ file `.pdb`** để thư mục chỉ có duy nhất 1 file `OCR_KazeHina.exe`.
- Khi build/publish, nếu gặp lỗi khóa file `MSB3026/MSB3027`, luôn tắt tiến trình đang chạy trước:
  `Stop-Process -Name "OCR_KazeHina" -Force -ErrorAction SilentlyContinue`

### 📌 Rule 3: Công thức Toán - Lý - Hóa Phải 100% MathML
- Mọi công thức Toán/Lý/Hóa bắt buộc đặt trong `<math xmlns="http://www.w3.org/1998/Math/MathML">`.
- Microsoft Word khi mở MathML sẽ tự động render thành **ô Equation nguyên bản**.
- Tuyệt đối không dùng cú pháp LaTeX thô (`$...$`, `\frac{}{}`), không dùng ảnh chụp công thức.
- Vectơ dùng `<mover><mi>B</mi><mo>&rarr;</mo></mover>`, căn thức dùng `<msqrt>`, phân số dùng `<mfrac>`.

### 📌 Rule 4: Tối ưu Chỉ Số Trên (Superscript) & Chỉ Số Dưới (Subscript)
- **Đơn vị đo lường có số mũ**: $m^2, m^3, cm^3, km^2, m/s^2, kg/m^3$ bắt buộc hiển thị chỉ số trên rõ ràng (dùng thẻ `<sup>` hoặc MathML `<msup>`), tuyệt đối không viết dính số thường như `m2, m3, km2`.
- **CSS chuẩn Word**: Trong `<style>` xuất file Word HTML, bắt buộc có:
  ```css
  sup { font-size: 8.5pt; vertical-align: super; mso-text-raise: 36%; line-height: 0; }
  sub { font-size: 8.5pt; vertical-align: sub; mso-text-raise: -25%; line-height: 0; }
  ```
  `mso-text-raise` đảm bảo Word hiển thị số mũ chuẩn xác và `line-height: 0` giúp không làm biến dạng khoảng cách dòng của văn bản.
- **Hậu xử lý tự động**: `GeminiOcrService.OptimizeScriptIndices()` tự động bắt Regex các đơn vị `m2, m3, cm2...` hoặc ký tự Unicode `², ³, ⁴, ₀, ₁` để chuẩn hóa sang `<sup>` và `<sub>`.

### 📌 Rule 5: Định dạng Đáp án Trắc nghiệm A, B, C, D
- BẮT BUỘC dùng **bảng bố cục ẩn viền hoàn toàn** (`border: none; border-collapse: collapse;`):
  - 4 đáp án trên 1 dòng: Bảng 1 hàng, 4 cột `width: 25%`.
  - 2 đáp án trên 1 dòng: Bảng 2 hàng, mỗi hàng 2 cột `width: 50%`.
- Không dùng phím Tab thông thường vì khi công thức dài sẽ đẩy đáp án D rớt xuống dòng thứ 2.

### 📌 Rule 6: Chống lỗi Kéo Dãn Chữ (Justify Bug) & Lỗi Bảng Mã (Encoding)
- Tuyệt đối không gán `text-align: justify;` vào thẻ `<body>` hoặc `<div>`. Chỉ gán cho `<p>` chứa văn bản xuôi dài từ 2 dòng trở lên.
- Giữa số và đơn vị đo bắt buộc dùng khoảng trắng cứng `&nbsp;` (ví dụ: `10&nbsp;cm`).
- Luôn lưu file Word HTML với encoding **UTF-8 có BOM** (`new UTF8Encoding(true)`) và thẻ `<meta charset="utf-8">` để Word mở trực tiếp không bao giờ hiện bảng chọn Encoding.

---

## 3. BÀI HỌC KINH NGHIỆM ĐÃ RÚT RA (LESSONS LEARNED)

### 💡 Bài học 1: Lỗi Thanh công cụ Zoom Xem Trước Bị Tràn / Che Nút Reset khi Minisize
- **Hiện tượng**: Khi cửa sổ ở kích thước nhỏ (`minisize`) hoặc tên file/thông số ảnh quá dài, nút **↺ (Reset/Fit)** ở góc phải thanh xem trước ảnh bị tràn ra ngoài viền.
- **Nguyên nhân**: Đặt thẻ tiêu đề và thông tin file bên trái trước cụm nút Zoom trong layout `DockPanel`. Khi chuỗi text dài, nó chiếm hết chiều ngang và đẩy cụm nút Zoom ra khỏi vùng hiển thị.
- **Giải pháp**:
  - Đặt `Border` chứa **Zoom Toolbar** với thuộc tính `DockPanel.Dock="Right"` lên **đầu tiên** trong `DockPanel`.
  - Đặt thuộc tính `TextTrimming="CharacterEllipsis"` cho thẻ thông tin file bên trái để text tự động co cụm lại, nhường trọn vẹn 100% không gian cố định cho cụm nút Zoom.

### 💡 Bài học 2: Hiệu ứng Thở (Breathing Glow Animation) Trên Nút Bấm
- **Hiện tượng**: Người dùng yêu cầu hiệu ứng thở khi hover chuột vào nút *Bắt đầu* và nút *Dán Ảnh*, nhưng không được làm co giãn kích thước nút (không dùng `ScaleTransform`). Đồng thời dải màu thở phải tràn viền 100% (Edge-to-Edge).
- **Nguyên nhân**: Ban đầu `Padding` nằm ở `OuterBorder`, khiến thẻ `PulseColorLayer` bên trong bị thụt lề vào trong khoảng 12px x 6px $\rightarrow$ dải màu thở không chạm sát mép viền bo góc.
- **Giải pháp**:
  - Giữ cố định kích thước khung viền, chỉ thay đổi `Opacity` của lớp `LinearGradientBrush` từ 0.15 đến 0.85 với `AutoReverse="True"`.
  - Chuyển `Padding` từ `OuterBorder` sang `Margin` của `ContentPresenter`, đặt `ClipToBounds="True"` trên viền ngoài để dải màu phát quang phủ kín tràn viền 100%.

### 💡 Bài học 3: Lỗi Ảnh Đen Xì Khi Chụp Màn Hình Từ Zalo
- **Hiện tượng**: Chụp màn hình bằng Zalo rồi bấm `Ctrl+V` vào app thì ảnh bị đen kịt.
- **Nguyên nhân**: Zalo ghi dữ liệu vào Clipboard dạng DIB 32-bit nhưng gán toàn bộ kênh Alpha (độ trong suốt) = 0. WPF hiểu là ảnh trong suốt 100%, khi vẽ lên nền tối sẽ thành màu đen.
- **Giải pháp**: Trong hàm `PasteFromClipboard()`, ưu tiên đọc định dạng `PNG` nếu có; nếu đọc `DIB`, tái tạo header BMP và ép chuyển đổi sang `PixelFormats.Bgr32` (Opaque) để loại bỏ hoàn toàn kênh Alpha.

### 💡 Bài học 4: Tránh Tồn Tại Nhiều Thư Mục AppPublish Trùng Tên
- **Hiện tượng**: Trong dự án vô tình sinh ra 2 thư mục `AppPublish` (một nằm ở ngoài thư mục cha `..\AppPublish` do lệnh publish cũ, một nằm trong `OcrWordApp\AppPublish`).
- **Giải pháp**:
  - Thống nhất duy nhất một thư mục xuất bản: `95.Ocr Pdf\AppPublish`.
  - Cấu hình `.gitignore` chặn thư mục `AppPublish/` và các file nhị phân `.exe, .pdb` để tránh commit nhầm file nặng lên Git.

### 💡 Bài học 5: Quản Lý Mô Hình Vision OCR (Gemini / DeepSeek)
- Các model Gemini cũ như `gemini-1.5-*`, `gemini-2.5-*` đã bị Google khai tử (trả về lỗi 404).
- Model Vision OCR hoạt động ổn định và phản hồi nhanh nhất hiện nay: **`gemini-3.1-flash-lite`** (mặc định số 1), kế đến là `gemini-3.6-flash`.
- Cơ chế Fallback thông minh: Ưu tiên DeepSeek xoay vòng nhiều key $\rightarrow$ nếu lỗi tự động chuyển sang Gemini dự phòng $\rightarrow$ nếu lỗi cả hai thì đánh dấu trang và xử lý tiếp trang sau, không làm sập ứng dụng.

### 💡 Bài học 6: Thay Thế Hộp Thoại Hệ Thống (MessageBox) Bằng Liquid Glass Modal Dialog
- **Hiện tượng**: `System.Windows.MessageBox` mặc định của Windows có màu trắng đục, góc vuông xơ cứng, làm gãy vỡ trải nghiệm thẩm mỹ cao cấp của giao diện Liquid Glass nền mờ.
- **Giải pháp**: Xây dựng `SuccessModalOverlay` ngay trong `MainWindow.xaml` với lớp Backdrop mờ nghệ thuật sâu (`BlurEffect`), Card kính trung tâm viền gradient phát quang (`GlassGradientBorderBrush`), icon trạng thái xanh ngọc và cặp nút bấm phong cách Liquid Glass đồng bộ (`InteractiveButtonNeon` cho "Mở Microsoft Word" và `GlassPlainButton` cho "Để sau").

