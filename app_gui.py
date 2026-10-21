import os
import sys
import threading
import tempfile
import time
import tkinter as tk
from tkinter import ttk, filedialog, messagebox
from pathlib import Path
from PIL import ImageGrab
from ocr_converter import load_images_from_paths, convert_images_to_word_html
from prompt_template import SYSTEM_PROMPT

CONFIG_FILE = Path.home() / ".ocr_word_gemini_key.txt"

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("OCR Đề Thi Sang Microsoft Word HTML (Chuẩn Khổ A4, MathML, Tab-stops)")
        self.geometry("900x700")
        self.minsize(800, 600)

        # Style cấu hình giao diện hiện đại
        self.style = ttk.Style(self)
        self.style.theme_use("clam")
        
        # Cấu hình hiệu ứng hover và style cho ttk widgets
        self.style.configure(
            "TButton",
            font=("Segoe UI", 9),
            padding=(10, 4),
            relief="flat",
            borderwidth=1
        )
        self.style.map(
            "TButton",
            background=[("active", "#e0e7ff"), ("pressed", "#c7d2fe"), ("!disabled", "#f1f5f9")],
            foreground=[("active", "#1e3a8a"), ("!disabled", "#0f172a")],
            relief=[("pressed", "sunken"), ("!pressed", "flat")]
        )
        self.style.configure(
            "TLabelframe",
            background="#f8fafc",
            relief="groove",
            borderwidth=1
        )
        self.style.configure(
            "TLabelframe.Label",
            font=("Segoe UI", 9, "bold"),
            foreground="#1e293b",
            background="#f8fafc"
        )
        
        self.selected_files = []
        self.api_key = self.load_saved_api_key()

        self.setup_ui()

    def load_saved_api_key(self) -> str:
        env_key = os.environ.get("GEMINI_API_KEY", "")
        if env_key:
            return env_key
        if CONFIG_FILE.exists():
            try:
                return CONFIG_FILE.read_text(encoding="utf-8").strip()
            except Exception:
                pass
        return ""

    def save_api_key(self, key: str):
        try:
            CONFIG_FILE.write_text(key.strip(), encoding="utf-8")
        except Exception:
            pass

    def setup_ui(self):
        # Header Frame
        header_frame = tk.Frame(self, bg="#1a365d", padx=15, pady=12)
        header_frame.pack(fill="x")

        title_lbl = tk.Label(
            header_frame,
            text="CHUYỂN ĐỔI ẢNH / PDF SANG WORD HTML CHUẨN IN ẤN",
            font=("Segoe UI", 14, "bold"),
            fg="white",
            bg="#1a365d"
        )
        title_lbl.pack(anchor="w")

        sub_lbl = tk.Label(
            header_frame,
            text="Hỗ trợ MathML, Tab-stops Office, Chống dãn từ Word, Tự động sửa lỗi bảng mã OCR iCity;",
            font=("Segoe UI", 9),
            fg="#cbd5e1",
            bg="#1a365d"
        )
        sub_lbl.pack(anchor="w", pady=(3, 0))

        # Main Content Frame
        main_frame = ttk.Frame(self, padding=12)
        main_frame.pack(fill="both", expand=True)

        # 1. API Key & Model Configuration
        config_group = ttk.LabelFrame(main_frame, text=" 1. Cấu hình Gemini AI ", padding=10)
        config_group.pack(fill="x", pady=(0, 10))

        key_row = ttk.Frame(config_group)
        key_row.pack(fill="x", pady=2)
        ttk.Label(key_row, text="Gemini API Key:", width=15).pack(side="left")
        self.key_entry = ttk.Entry(key_row, show="*")
        self.key_entry.insert(0, self.api_key)
        self.key_entry.pack(side="left", fill="x", expand=True, padx=5)

        model_row = ttk.Frame(config_group)
        model_row.pack(fill="x", pady=4)
        ttk.Label(model_row, text="Mô hình Gemini:", width=15).pack(side="left")
        self.model_combo = ttk.Combobox(model_row, values=["gemini-3.1-flash-lite", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.7-flash", "gemini-3.8-flash"], state="readonly")
        self.model_combo.set("gemini-3.1-flash-lite")
        self.model_combo.pack(side="left", padx=5)

        ttk.Label(model_row, text="(Khuyên dùng gemini-3.1-flash-lite: nhẹ, phản hồi tức thì & nhận diện MathML rất tốt)", foreground="#666").pack(side="left", padx=5)

        # 2. File Selection Group
        file_group = ttk.LabelFrame(main_frame, text=" 2. Chọn hình ảnh đề thi / File PDF ", padding=10)
        file_group.pack(fill="both", expand=True, pady=(0, 10))

        btn_bar = ttk.Frame(file_group)
        btn_bar.pack(fill="x", pady=(0, 6))

        ttk.Button(btn_bar, text="➕ Thêm Ảnh (JPG, PNG...)", command=self.add_images).pack(side="left", padx=(0, 5))
        ttk.Button(btn_bar, text="📄 Thêm File PDF", command=self.add_pdf).pack(side="left", padx=5)
        ttk.Button(btn_bar, text="❌ Xóa Mục Chọn", command=self.remove_selected).pack(side="left", padx=5)
        ttk.Button(btn_bar, text="🧹 Xóa Hết", command=self.clear_all).pack(side="left", padx=5)

        # Listbox danh sách file
        list_container = ttk.Frame(file_group)
        list_container.pack(fill="both", expand=True)

        self.file_listbox = tk.Listbox(
            list_container,
            selectmode=tk.EXTENDED,
            font=("Consolas", 10),
            bg="#ffffff",
            selectbackground="#0284c7",
            selectforeground="#ffffff",
            activestyle="none",
            relief="solid",
            borderwidth=1,
            highlightthickness=1,
            highlightcolor="#38bdf8",
            highlightbackground="#cbd5e1"
        )
        self.file_listbox.pack(side="left", fill="both", expand=True)

        scrollbar = ttk.Scrollbar(list_container, orient="vertical", command=self.file_listbox.yview)
        scrollbar.pack(side="right", fill="y")
        self.file_listbox.config(yscrollcommand=scrollbar.set)

        # Context Menu (Menu chuột phải) cho danh sách file
        self.context_menu = tk.Menu(self, tearoff=0)
        self.context_menu.add_command(label="📋 Dán ảnh từ Clipboard (Ctrl+V)", command=self.paste_image_from_clipboard)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="➕ Thêm file ảnh...", command=self.add_images)
        self.context_menu.add_command(label="📄 Thêm file PDF...", command=self.add_pdf)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="❌ Xóa các mục đã chọn", command=self.remove_selected)
        self.context_menu.add_command(label="🧹 Xóa toàn bộ danh sách", command=self.clear_all)

        # Ràng buộc sự kiện click chuột phải (Windows: <Button-3>) & phím tắt Ctrl+V, Delete
        self.file_listbox.bind("<Button-3>", self.show_context_menu)
        self.bind("<Control-v>", lambda e: self.paste_image_from_clipboard())
        self.bind("<Control-V>", lambda e: self.paste_image_from_clipboard())
        self.file_listbox.bind("<Delete>", lambda e: self.remove_selected())

        # 3. Execution & Status Group
        action_group = ttk.Frame(main_frame)
        action_group.pack(fill="x", pady=5)

        self.btn_convert = tk.Button(
            action_group,
            text="🚀 BẮT ĐẦU CHUYỂN ĐỔI SANG WORD HTML (.doc)",
            font=("Segoe UI", 11, "bold"),
            bg="#0284c7",
            fg="white",
            activebackground="#0369a1",
            activeforeground="white",
            padx=15,
            pady=10,
            cursor="hand2",
            relief="flat",
            command=self.start_conversion
        )
        self.btn_convert.pack(fill="x")

        # Hiệu ứng hover cho nút chuyển đổi chính
        self.btn_convert.bind("<Enter>", lambda e: self.btn_convert.config(bg="#0369a1") if str(self.btn_convert["state"]) != "disabled" else None)
        self.btn_convert.bind("<Leave>", lambda e: self.btn_convert.config(bg="#0284c7") if str(self.btn_convert["state"]) != "disabled" else None)

        # Progress & Status Bar
        self.status_var = tk.StringVar(value="Sẵn sàng.")
        self.status_label = ttk.Label(main_frame, textvariable=self.status_var, font=("Segoe UI", 9, "italic"))
        self.status_label.pack(anchor="w", pady=(5, 0))

        self.progress_bar = ttk.Progressbar(main_frame, mode="indeterminate")
        self.progress_bar.pack(fill="x", pady=(2, 0))

    def show_context_menu(self, event):
        # Nếu click chuột phải vào một dòng cụ thể mà dòng đó chưa được chọn, chuyển vùng chọn sang dòng đó
        clicked_idx = self.file_listbox.nearest(event.y)
        if clicked_idx >= 0 and self.file_listbox.size() > 0:
            if clicked_idx not in self.file_listbox.curselection():
                self.file_listbox.selection_clear(0, tk.END)
                self.file_listbox.selection_set(clicked_idx)
                self.file_listbox.activate(clicked_idx)
        
        # Bật/tắt trạng thái của nút Xóa tùy theo có phần tử được chọn hay danh sách trống
        has_selection = bool(self.file_listbox.curselection())
        has_items = self.file_listbox.size() > 0
        self.context_menu.entryconfig("❌ Xóa các mục đã chọn", state="normal" if has_selection else "disabled")
        self.context_menu.entryconfig("🧹 Xóa toàn bộ danh sách", state="normal" if has_items else "disabled")

        self.context_menu.tk_popup(event.x_root, event.y_root)

    def paste_image_from_clipboard(self):
        try:
            img = ImageGrab.grabclipboard()
            if img is None:
                messagebox.showinfo("Clipboard", "Không tìm thấy dữ liệu hình ảnh trong Clipboard.\n\nHãy chụp màn hình (Win + Shift + S) hoặc Copy một ảnh rồi dán lại!")
                return

            # Nếu clipboard chứa danh sách file path
            if isinstance(img, list):
                added_count = 0
                for path_str in img:
                    p = Path(path_str)
                    if p.suffix.lower() in [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".pdf"]:
                        if str(p) not in self.selected_files:
                            self.selected_files.append(str(p))
                            self.file_listbox.insert(tk.END, str(p))
                            added_count += 1
                if added_count > 0:
                    self.update_status(f"Đã dán {added_count} file từ clipboard.")
                else:
                    messagebox.showinfo("Clipboard", "Các file trong clipboard không đúng định dạng ảnh hoặc PDF.")
                return

            # Nếu clipboard là đối tượng ảnh chụp màn hình / copy ảnh trực tiếp
            temp_dir = Path(tempfile.gettempdir()) / "ocr_word_pasted_images"
            temp_dir.mkdir(parents=True, exist_ok=True)
            timestamp = int(time.time() * 1000)
            pasted_file = temp_dir / f"pasted_image_{timestamp}.png"
            img.save(pasted_file, format="PNG")

            pasted_path_str = str(pasted_file)
            self.selected_files.append(pasted_path_str)
            self.file_listbox.insert(tk.END, f"[Ảnh dán] {pasted_file.name} -> {pasted_path_str}")
            self.update_status(f"Đã dán ảnh thành công: {pasted_file.name}")

        except Exception as e:
            messagebox.showerror("Lỗi dán ảnh", f"Không thể lấy ảnh từ Clipboard: {str(e)}")

    def add_images(self):
        files = filedialog.askopenfilenames(
            title="Chọn các ảnh đề thi ôn tập",
            filetypes=[("Hình ảnh", "*.jpg *.jpeg *.png *.webp *.bmp *.tif *.tiff")]
        )
        for f in files:
            if f not in self.selected_files:
                self.selected_files.append(f)
                self.file_listbox.insert(tk.END, f)

    def add_pdf(self):
        files = filedialog.askopenfilenames(
            title="Chọn file PDF",
            filetypes=[("Tập tin PDF", "*.pdf")]
        )
        for f in files:
            if f not in self.selected_files:
                self.selected_files.append(f)
                self.file_listbox.insert(tk.END, f)

    def remove_selected(self):
        indices = list(self.file_listbox.curselection())
        indices.reverse()
        for idx in indices:
            del self.selected_files[idx]
            self.file_listbox.delete(idx)

    def clear_all(self):
        self.selected_files.clear()
        self.file_listbox.delete(0, tk.END)

    def update_status(self, msg: str):
        self.status_var.set(msg)

    def start_conversion(self):
        key = self.key_entry.get().strip()
        if not key:
            messagebox.showwarning("Thiếu API Key", "Vui lòng nhập Gemini API Key để tiếp tục.")
            return

        if not self.selected_files:
            messagebox.showwarning("Chưa chọn file", "Vui lòng thêm ít nhất một ảnh hoặc file PDF.")
            return

        self.save_api_key(key)

        save_path = filedialog.asksaveasfilename(
            title="Lưu file Word HTML",
            defaultextension=".doc",
            filetypes=[
                ("Microsoft Word Document (.doc)", "*.doc"),
                ("HTML Document (.html)", "*.html")
            ]
        )
        if not save_path:
            return

        # Vô hiệu hóa nút và kích hoạt progress bar
        self.btn_convert.config(state="disabled")
        self.progress_bar.start(10)
        
        # Chạy trong thread riêng để không bị đơ UI
        thread = threading.Thread(
            target=self.run_conversion_worker,
            args=(key, self.selected_files.copy(), self.model_combo.get(), save_path),
            daemon=True
        )
        thread.start()

    def run_conversion_worker(self, api_key: str, files: list, model: str, output_path: str):
        try:
            self.update_status("Đang đọc dữ liệu ảnh/PDF...")
            images = load_images_from_paths(files)
            
            self.update_status(f"Đã nạp {len(images)} trang ảnh. Đang gửi dữ liệu đến Gemini ({model})...")
            
            html_content = convert_images_to_word_html(
                api_key=api_key,
                images=images,
                model_name=model,
                progress_callback=self.update_status
            )
            
            self.update_status("Đang lưu file xuất ra ổ đĩa...")
            with open(output_path, "w", encoding="utf-8") as f:
                f.write(html_content)

            self.update_status(f"Hoàn tất! File đã được lưu tại: {output_path}")
            self.after(0, lambda: messagebox.showinfo(
                "Thành công", 
                f"Đã chuyển đổi thành công sang file:\n{output_path}\n\nBạn có thể nhấp chuột phải chọn 'Open with Word' để mở trực tiếp!"
            ))

        except Exception as e:
            self.update_status(f"Lỗi: {str(e)}")
            self.after(0, lambda: messagebox.showerror("Lỗi xử lý", f"Đã xảy ra lỗi:\n{str(e)}"))
        finally:
            self.after(0, self.finish_conversion)

    def finish_conversion(self):
        self.progress_bar.stop()
        self.btn_convert.config(state="normal")


if __name__ == "__main__":
    app = App()
    app.mainloop()
