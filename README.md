# Phần Mềm OCR Tài Liệu & Đề Thi Sang Microsoft Word HTML

Dự án chuyên dụng để chuyển đổi hình ảnh hoặc file PDF chứa đề thi, bài tập Vật Lý/Toán Học sang **duy nhất một file HTML tương thích hoàn hảo 100% với Microsoft Word** (mở trực tiếp bằng Word hoặc lưu dưới đuôi `.doc`).

---

## 🌟 Tính Năng Nổi Bật Đáp Ứng Đúng Chuẩn

1. **Chuẩn Khổ In A4 & Chống Kéo Dãn Chữ Word:**
   - Cấu hình khổ in A4 tự động mở ở chế độ *Print Layout* trong Word.
   - Thẻ `<div class="Section1">` bao trọn văn bản.
   - Các dòng gạch đầu dòng (`-`) nằm riêng từng thẻ `<p>` độc lập, thụt lề chuẩn `margin: 2pt 0 2pt 18pt;`.
   - Ngăn chặn lỗi giãn khoảng cách chữ bằng cách cấm `justify` ở các thẻ cha.
   - Dùng khoảng trắng cứng `&nbsp;` giữa số và đơn vị đo (ví dụ: `10&nbsp;cm`, `0,05&nbsp;T`).

2. **Định Dạng Đáp Án Trắc Nghiệm Bằng Điểm Dừng Tab Word (`mso-tab-count:1`):**
   - **Tuyệt đối không dùng table** cho các lựa chọn A, B, C, D để người dùng dễ kéo thước và chỉnh sửa font.
   - Tự động chia 4 đáp án trên 1 dòng hoặc 2 đáp án/dòng kèm tọa độ `tab-stops` chính xác.

3. **Công Thức Toán - Lý Chuẩn MathML:**
   - Dùng **MathML** (tương thích nguyên bản Word mà không cần cài thêm MathType hay JavaScript):
     - Véctơ: `<math xmlns="http://www.w3.org/1998/Math/MathML"><mover><mi>B</mi><mo>&rarr;</mo></mover></math>`
     - Căn bậc hai: `<math xmlns="http://www.w3.org/1998/Math/MathML"><msqrt><mn>3</mn></msqrt></math>`
     - Phân số 2 tầng: `<math xmlns="http://www.w3.org/1998/Math/MathML"><mfrac><mi>&pi;</mi><mn>2</mn></mfrac></math>`
   - Các ký tự đặc biệt: `&alpha;`, `&Phi;`, `&omega;`, `&pi;`, `&sdot;`.

4. **Tự Động Sửa Lỗi Bảng Mã OCR & In Ấn:**
   - Tự động chuẩn hóa các lỗi vỡ chữ như `iCity;` / `ỉCity;` thành `iệ` hoặc `hiệ` (diện tích, hiện, xuất hiện, hiệu dụng...).
   - Khắc phục các lỗi in ấn: `thểi gian` → `thời gian`, `đuểng sức` → `đường sức`, `từ trưếng` → `từ trường`...

5. **Giữ Chỗ Đồ Thị & Chống Cắt Trang:**
   - Mỗi câu hỏi được bọc trong khối `<div class="avoid-break">` chống bị ngắt đôi giữa 2 trang.
   - Khung placeholder viền đứt cho các sơ đồ, đồ thị hình vẽ.

---

## 🚀 Hướng Dẫn Sử Dụng

### Cách 1: Sử Dụng Giao Diện Đồ Họa (GUI)
Nhấp đúp chuột vào file:
```
Chay_Ung_Dung.bat
```
hoặc chạy qua dòng lệnh:
```bash
python app_gui.py
```
1. Nhập **Gemini API Key** (chỉ cần nhập lần đầu, phần mềm sẽ tự lưu).
2. Nhấn nút **Thêm Ảnh** hoặc **Thêm PDF** để chọn tài liệu cần bóc tách.
3. Bấm **BẮT ĐẦU CHUYỂN ĐỔI SANG WORD HTML** và chọn nơi lưu file (`.doc` hoặc `.html`).
4. Mở file kết quả trực tiếp bằng Microsoft Word!

### Cách 2: Sử Dụng Dòng Lệnh (CLI)
```bash
python cli.py de_thi_trang1.png de_thi_trang2.png -o ket_qua.doc
```
Hoặc với file PDF:
```bash
python cli.py tai_lieu.pdf -o ket_qua.doc
```
