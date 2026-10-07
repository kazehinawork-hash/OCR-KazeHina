using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace OcrWordApp;

public class FileItem : INotifyPropertyChanged
{
    private int _indexNumber;
    public int IndexNumber
    {
        get => _indexNumber;
        set { _indexNumber = value; OnPropertyChanged(nameof(IndexNumber)); }
    }

    public string FilePath { get; set; } = "";
    public string DisplayName => Path.GetFileName(FilePath);
    public string FileSizeText { get; set; } = "";
    public BitmapSource? Thumbnail { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : FluentWindow
{
    private static readonly string KeyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ocr_word_gemini_key.txt"
    );

    private readonly ObservableCollection<FileItem> _fileItems = new();
    private CancellationTokenSource? _debounceCts;

    public MainWindow()
    {
        InitializeComponent();
        LstFiles.ItemsSource = _fileItems;
        _fileItems.CollectionChanged += (s, e) =>
        {
            UpdateEmptyHint();
            ReindexItems();
        };

        // Lắng nghe sự kiện bàn phím phím tắt Ctrl + V
        this.KeyDown += MainWindow_KeyDown;

        LoadSavedApiKey();

        if (!string.IsNullOrWhiteSpace(TxtApiKey.Password))
        {
            _ = FetchModelsAsync(TxtApiKey.Password.Trim());
        }
        else
        {
            CmbModel.Items.Add("gemini-3.1-flash-lite");
            CmbModel.Items.Add("gemini-3.6-flash");
            CmbModel.Items.Add("gemini-3.5-flash-lite");
            CmbModel.Items.Add("gemini-3.7-flash");
            CmbModel.SelectedIndex = 0;
        }
    }

    private void ReindexItems()
    {
        for (int i = 0; i < _fileItems.Count; i++)
        {
            _fileItems[i].IndexNumber = i + 1;
        }
    }

    private void AddFileItem(string path)
    {
        if (_fileItems.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;

        BitmapSource? thumb = null;
        string sizeText = "";

        try
        {
            var fi = new FileInfo(path);
            sizeText = fi.Length > 1024 * 1024 
                ? $"{fi.Length / (1024.0 * 1024.0):F1} MB" 
                : $"{fi.Length / 1024.0:F0} KB";

            var ext = fi.Extension.ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")
            {
                // Đọc trực tiếp qua FileStream để nạp toàn bộ vào RAM, tránh lỗi lock file hoặc lỗi UriSource trên máy khác
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 100; // Siêu nhẹ cho thumbnail
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                    bmp.Freeze();
                    thumb = bmp;
                }
            }
        }
        catch { }

        var item = new FileItem
        {
            FilePath = path,
            FileSizeText = sizeText,
            Thumbnail = thumb,
            IndexNumber = _fileItems.Count + 1
        };

        _fileItems.Add(item);
        LstFiles.SelectedItem = item;
    }

    private void LstFiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LstFiles.SelectedItem is FileItem item)
        {
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
            {
                ImgPreview.Source = null;
                PreviewEmptyHint.Visibility = Visibility.Visible;
                TxtPreviewInfo.Text = "Không tìm thấy tệp ảnh trên máy.";
                return;
            }

            var ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
            if (ext == ".pdf")
            {
                ImgPreview.Source = null;
                PreviewEmptyHint.Visibility = Visibility.Visible;
                TxtPreviewInfo.Text = $"Tài liệu PDF: {item.DisplayName} (Sẽ tự động bóc tách các trang khi chuyển đổi)";
                return;
            }

            try
            {
                // Nạp file qua FileStream vào RAM và gỡ bỏ khóa file để tương thích 100% mọi máy tính
                using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 1600; // Hiển thị sắc nét trên mọi độ phân giải màn hình
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();

                ImgPreview.Source = bmp;
                PreviewEmptyHint.Visibility = Visibility.Collapsed;
                TxtPreviewInfo.Text = $"Trang {item.IndexNumber} • {bmp.PixelWidth}x{bmp.PixelHeight}px • {item.DisplayName}";
            }
            catch (Exception ex)
            {
                try
                {
                    // Fallback bằng System.Drawing nếu Windows Media Codec bị thiếu trên máy người dùng
                    using var gdi = System.Drawing.Image.FromFile(item.FilePath);
                    using var ms = new MemoryStream();
                    gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    ImgPreview.Source = bmp;
                    PreviewEmptyHint.Visibility = Visibility.Collapsed;
                    TxtPreviewInfo.Text = $"Trang {item.IndexNumber} • {gdi.Width}x{gdi.Height}px • {item.DisplayName}";
                }
                catch
                {
                    ImgPreview.Source = null;
                    PreviewEmptyHint.Visibility = Visibility.Visible;
                    TxtPreviewInfo.Text = $"Không thể nạp xem trước: {ex.Message}";
                }
            }
        }
        else
        {
            ImgPreview.Source = null;
            PreviewEmptyHint.Visibility = Visibility.Visible;
            TxtPreviewInfo.Text = "";
        }
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = LstFiles.SelectedIndex;
        if (idx > 0)
        {
            _fileItems.Move(idx, idx - 1);
            LstFiles.SelectedIndex = idx - 1;
        }
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = LstFiles.SelectedIndex;
        if (idx >= 0 && idx < _fileItems.Count - 1)
        {
            _fileItems.Move(idx, idx + 1);
            LstFiles.SelectedIndex = idx + 1;
        }
    }

    private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.V && 
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            if (!TxtApiKey.IsFocused)
            {
                PasteFromClipboard();
                e.Handled = true;
            }
        }
    }

    private void BtnPasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        PasteFromClipboard();
    }

    private void PasteFromClipboard()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "OcrWordApp_Screenshots");
            Directory.CreateDirectory(tempDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var filePath = Path.Combine(tempDir, $"Screenshot_{timestamp}.png");

            // 1. Dán từ hình ảnh Clipboard (Xử lý triệt để lỗi ảnh đen xì từ Zalo / Windows Snipping Tool)
            var dataObj = System.Windows.Clipboard.GetDataObject();
            if (dataObj != null)
            {
                // Cách 1: Thử đọc trực tiếp stream PNG từ Clipboard (Zalo thường đưa luồng PNG gốc vào đây)
                if (dataObj.GetDataPresent("PNG", true))
                {
                    if (dataObj.GetData("PNG") is Stream pngStream)
                    {
                        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                        pngStream.CopyTo(fileStream);
                        fileStream.Flush();
                        AddFileItem(filePath);
                        TxtStatus.Text = $"Đã dán thành công ảnh chụp màn hình Zalo ({Path.GetFileName(filePath)})";
                        return;
                    }
                }

                // Cách 2: Đọc DeviceIndependentBitmap (DIB) từ Clipboard (Zalo gửi định dạng DIB 32-bit với Alpha = 0)
                if (dataObj.GetDataPresent(System.Windows.DataFormats.Dib, true))
                {
                    if (dataObj.GetData(System.Windows.DataFormats.Dib) is MemoryStream dibStream)
                    {
                        var dibBytes = dibStream.ToArray();
                        if (dibBytes.Length >= 40)
                        {
                            try
                            {
                                // Tái tạo header BMP 14 bytes cho DIB để BitmapImage giải mã đúng
                                int width = BitConverter.ToInt32(dibBytes, 4);
                                int height = BitConverter.ToInt32(dibBytes, 8);
                                short bpp = BitConverter.ToInt16(dibBytes, 14);

                                using var msBmp = new MemoryStream();
                                using (var bw = new BinaryWriter(msBmp))
                                {
                                    // Header BMP: BM (2 bytes)
                                    bw.Write((byte)'B');
                                    bw.Write((byte)'M');
                                    bw.Write((int)(14 + dibBytes.Length)); // File size
                                    bw.Write((short)0);
                                    bw.Write((short)0);
                                    bw.Write((int)(14 + 40)); // Offset to pixel data (BITMAPINFOHEADER = 40)
                                    bw.Write(dibBytes);
                                }

                                msBmp.Position = 0;
                                var bmpDecoder = BitmapDecoder.Create(msBmp, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                                if (bmpDecoder.Frames.Count > 0)
                                {
                                    var frame = bmpDecoder.Frames[0];
                                    // Ép chuyển sang định dạng Bgr32 để loại bỏ hoàn toàn kênh Alpha bị 0 (chống đen xì 100%)
                                    var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                                    converted.Freeze();

                                    using var fs = new FileStream(filePath, FileMode.Create);
                                    var enc = new PngBitmapEncoder();
                                    enc.Frames.Add(BitmapFrame.Create(converted));
                                    enc.Save(fs);

                                    AddFileItem(filePath);
                                    TxtStatus.Text = $"Đã dán thành công ảnh chụp màn hình Zalo ({Path.GetFileName(filePath)})";
                                    return;
                                }
                            }
                            catch
                            {
                                // Bỏ qua nếu DIB giải mã thất bại và đi tiếp tới cách 3
                            }
                        }
                    }
                }

                // Cách 3: Fallback qua WPF GetImage() nếu hai cách trên không khớp
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var imageSource = System.Windows.Clipboard.GetImage();
                    if (imageSource != null)
                    {
                        // Khử kênh Alpha đen xì: ép chuyển sang Bgr32 (nền opaque)
                        var formattedBmp = new FormatConvertedBitmap(imageSource, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                        using (var fileStream = new FileStream(filePath, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(formattedBmp));
                            encoder.Save(fileStream);
                        }

                        AddFileItem(filePath);
                        TxtStatus.Text = $"Đã dán thành công ảnh chụp màn hình ({Path.GetFileName(filePath)})";
                        return;
                    }
                }
            }

            // 2. Dán danh sách file copy từ File Explorer
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var fileDropList = System.Windows.Clipboard.GetFileDropList();
                var supported = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".pdf" };
                int addedCount = 0;
                foreach (var file in fileDropList)
                {
                    if (file != null)
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (supported.Contains(ext))
                        {
                            AddFileItem(file);
                            addedCount++;
                        }
                    }
                }
                if (addedCount > 0)
                {
                    TxtStatus.Text = $"Đã dán thành công {addedCount} tệp từ Clipboard.";
                    return;
                }
            }

            System.Windows.MessageBox.Show(
                "Không tìm thấy hình ảnh hoặc tệp tài liệu trong Clipboard.\n\nHãy chụp màn hình (Win + Shift + S hoặc PrintScreen) rồi bấm Ctrl + V lại nhé!",
                "Clipboard trống",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Lỗi khi đọc Clipboard: {ex.Message}", "Lỗi", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateEmptyHint()
    {
        EmptyHintPanel.Visibility = _fileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSavedApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            TxtApiKey.Password = envKey;
            return;
        }

        if (File.Exists(KeyFilePath))
        {
            try
            {
                TxtApiKey.Password = File.ReadAllText(KeyFilePath).Trim();
            }
            catch { }
        }
    }

    private void SaveApiKey(string key)
    {
        try
        {
            File.WriteAllText(KeyFilePath, key.Trim());
        }
        catch { }
    }

    private void TxtApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var key = TxtApiKey.Password.Trim();
        if (key.Length < 25) return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Delay(600, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Dispatcher.Invoke(() =>
                {
                    SaveApiKey(key);
                    _ = FetchModelsAsync(key);
                });
            }
        }, token);
    }

    private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
    {
        var key = TxtApiKey.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            System.Windows.MessageBox.Show("Vui lòng nhập Gemini API Key để quét danh sách mô hình.", "Thiếu API Key", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SaveApiKey(key);
        await FetchModelsAsync(key);
    }

    private async Task FetchModelsAsync(string key)
    {
        try
        {
            BtnRefreshModels.IsEnabled = false;
            TxtStatus.Text = "Đang quét danh sách mô hình từ Gemini API...";
            var models = await GeminiOcrService.GetAvailableModelsAsync(key);

            if (models.Count > 0)
            {
                var previousSelection = CmbModel.SelectedItem?.ToString();
                CmbModel.Items.Clear();
                foreach (var m in models)
                {
                    CmbModel.Items.Add(m);
                }

                if (!string.IsNullOrEmpty(previousSelection) && models.Contains(previousSelection))
                {
                    CmbModel.SelectedItem = previousSelection;
                }
                else
                {
                    var defaultModel = models.FirstOrDefault(m => m.Equals("gemini-3.1-flash-lite", StringComparison.OrdinalIgnoreCase))
                                    ?? models.FirstOrDefault(m => m.Contains("flash")) 
                                    ?? models[0];
                    CmbModel.SelectedItem = defaultModel;
                }

                TxtStatus.Text = $"Đã quét thành công {models.Count} mô hình Gemini từ API Key của bạn.";
            }
            else
            {
                TxtStatus.Text = "Không tìm thấy mô hình phù hợp từ API Key.";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Lỗi khi quét mô hình: {ex.Message}";
        }
        finally
        {
            BtnRefreshModels.IsEnabled = true;
        }
    }

    private void BtnAddImages_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Chọn hình ảnh bài tập / đề thi",
            Filter = "Hình ảnh (*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif)|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff|Tất cả tệp (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AddFileItem(file);
            }
        }
    }

    private void BtnAddPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Chọn file tài liệu PDF",
            Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AddFileItem(file);
            }
        }
    }

    private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LstFiles.SelectedItem is FileItem selected)
        {
            _fileItems.Remove(selected);
        }
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        _fileItems.Clear();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var supported = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".pdf" };
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (supported.Contains(ext))
                {
                    AddFileItem(f);
                }
            }
        }
    }

    private async void BtnConvert_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = TxtApiKey.Password.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            System.Windows.MessageBox.Show("Vui lòng nhập Gemini API Key để tiếp tục.", "Thiếu API Key", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtApiKey.Focus();
            return;
        }

        if (_fileItems.Count == 0)
        {
            System.Windows.MessageBox.Show("Vui lòng thêm ít nhất một ảnh hoặc file PDF để chuyển đổi.", "Chưa chọn file", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveApiKey(apiKey);

        var saveDlg = new SaveFileDialog
        {
            Title = "Chọn nơi lưu tài liệu Microsoft Word",
            Filter = "Tài liệu Microsoft Word (*.doc)|*.doc|Tệp HTML Word (*.html)|*.html",
            DefaultExt = ".doc",
            FileName = "DeThi_ChuyenDoi.doc"
        };

        if (saveDlg.ShowDialog() != true)
        {
            return;
        }

        var outputPath = saveDlg.FileName;
        var selectedModel = CmbModel.SelectedItem?.ToString() ?? "gemini-3.1-flash-lite";
        var filePathsSnapshot = _fileItems.Select(f => f.FilePath).ToList();

        // Cập nhật trạng thái UI
        BtnConvert.IsEnabled = false;
        PrgBar.Visibility = Visibility.Visible;
        PrgBar.IsIndeterminate = true;
        BusyBadge.Visibility = Visibility.Visible;
        TxtTitleBusy.Text = "Đang xử lý...";
        TxtStatus.Text = "Đang tải và chuẩn bị dữ liệu hình ảnh...";

        try
        {
            var htmlOutput = await Task.Run(async () =>
            {
                var images = GeminiOcrService.LoadImages(filePathsSnapshot);
                return await GeminiOcrService.ConvertToWordHtmlAsync(
                    apiKey,
                    images,
                    selectedModel,
                    progress => Dispatcher.Invoke(() =>
                    {
                        TxtStatus.Text = progress;
                        TxtTitleBusy.Text = progress;
                    })
                );
            });

            TxtStatus.Text = "Đang lưu file ra ổ đĩa...";
            // Ghi file với UTF-8 có BOM (Byte Order Mark) để Microsoft Word mở tự động 100% không hiện bảng hỏi File Conversion
            await File.WriteAllTextAsync(outputPath, htmlOutput, new System.Text.UTF8Encoding(true));

            TxtStatus.Text = $"Hoàn tất: {Path.GetFileName(outputPath)}";
            PrgBar.Visibility = Visibility.Hidden;
            BusyBadge.Visibility = Visibility.Collapsed;

            var res = System.Windows.MessageBox.Show(
                $"Chuyển đổi thành công!\n\nTập tin đã lưu tại:\n{outputPath}\n\nBạn có muốn mở ngay tập tin này bằng Microsoft Word không?",
                "Hoàn tất chuyển đổi",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (res == System.Windows.MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = outputPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Có lỗi xảy ra trong quá trình xử lý.";
            System.Windows.MessageBox.Show($"Đã xảy ra lỗi:\n\n{ex.Message}", "Lỗi", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnConvert.IsEnabled = true;
            PrgBar.Visibility = Visibility.Hidden;
            BusyBadge.Visibility = Visibility.Collapsed;
        }
    }
}