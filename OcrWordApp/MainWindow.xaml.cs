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

    private static string? FindKeyFile(string fileName)
    {
        // Ưu tiên file cạnh file .exe (bản đóng gói), sau đó dò ngược lên các thư mục cha
        // để đọc trực tiếp file nguồn trong thư mục dự án khi chạy Debug/Release.
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 5 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindApiKeysFile() => FindKeyFile("api_keys.txt");

    private readonly List<string> _activeApiKeys = new();
    private readonly List<string> _activeDeepSeekKeys = new();
    private int _keyRotationIndex = 0;

    private readonly ObservableCollection<FileItem> _fileItems = new();

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
        LoadDeepSeekKey();

        if (_activeDeepSeekKeys.Count > 0)
        {
            TxtStatus.Text += "  🛟 Đã có DeepSeek dự phòng.";
        }

        if (_activeApiKeys.Count > 0)
        {
            _ = FetchModelsAsync(_activeApiKeys[0]);
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

    public const int MaxAllowedImages = 5;

    private void UpdateFileCountBadge()
    {
        if (TxtFileCountBadge != null)
        {
            TxtFileCountBadge.Text = $"{_fileItems.Count}/{MaxAllowedImages} ảnh (Tối đa {MaxAllowedImages} ảnh/lần)";
            if (_fileItems.Count >= MaxAllowedImages)
            {
                TxtFileCountBadge.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
            else
            {
                TxtFileCountBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8));
            }
        }
    }

    private void ReindexItems()
    {
        for (int i = 0; i < _fileItems.Count; i++)
        {
            _fileItems[i].IndexNumber = i + 1;
        }
        UpdateFileCountBadge();
    }

    private void AddFileItem(string path)
    {
        if (_fileItems.Count >= MaxAllowedImages)
        {
            System.Windows.MessageBox.Show(
                $"Khuyến nghị tối đa {MaxAllowedImages} ảnh/trang cho mỗi lần chuyển đổi để đảm bảo AI bóc tách đầy đủ 100% không bị quá tải hoặc rớt câu.\n\nBạn hãy bấm 'Bắt đầu' để xuất tài liệu hiện tại ra Word trước, sau đó tiếp tục xử lý các ảnh tiếp theo nhé!",
                "Đã đạt giới hạn ảnh tối đa",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }

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
            PasteFromClipboard();
            e.Handled = true;
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
        _activeApiKeys.Clear();
        _activeDeepSeekKeys.Clear();

        // 1. Ưu tiên cao nhất: Đọc file api_keys.txt (cạnh .exe, hoặc file nguồn trong thư mục dự án).
        //    Tự phân loại: dòng bắt đầu bằng 'sk-' là key DeepSeek dự phòng, còn lại là key Gemini.
        var apiKeysPath = FindApiKeysFile();
        if (apiKeysPath != null)
        {
            try
            {
                var lines = File.ReadAllLines(apiKeysPath)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#") && l.Length >= 20)
                    .Distinct()
                    .ToList();

                foreach (var key in lines)
                {
                    if (key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_activeDeepSeekKeys.Contains(key)) _activeDeepSeekKeys.Add(key);
                    }
                    else
                    {
                        if (!_activeApiKeys.Contains(key)) _activeApiKeys.Add(key);
                    }
                }

                if (_activeApiKeys.Count > 0)
                {
                    var deepNote = (_activeDeepSeekKeys.Count > 0) ? $" + {_activeDeepSeekKeys.Count} DeepSeek Key dự phòng" : "";
                    TxtStatus.Text = $"🔑 Đã nạp {_activeApiKeys.Count} Gemini Key{deepNote} từ 'api_keys.txt' (Tự động xoay vòng).";
                    return;
                }
            }
            catch { }
        }

        // 2. Dự phòng: Đọc biến môi trường
        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            _activeApiKeys.Add(envKey.Trim());
            return;
        }

        // 3. Dự phòng: Đọc file key cá nhân đã lưu trong thư mục User
        if (File.Exists(KeyFilePath))
        {
            try
            {
                var savedKeys = File.ReadAllLines(KeyFilePath)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#") && l.Length >= 25)
                    .Distinct()
                    .ToList();

                if (savedKeys.Count > 0)
                {
                    _activeApiKeys.AddRange(savedKeys);
                    if (_activeApiKeys.Count > 1)
                    {
                        TxtStatus.Text = $"🔑 Đã nạp {_activeApiKeys.Count} API Key từ cấu hình đã lưu.";
                    }
                }
            }
            catch { }
        }
    }

    private void LoadDeepSeekKey()
    {
        // Đã có key DeepSeek từ api_keys.txt (dòng bắt đầu bằng 'sk-') thì thôi
        if (_activeDeepSeekKeys.Count > 0) return;

        // Dự phòng 1: Biến môi trường
        var envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            _activeDeepSeekKeys.Add(envKey.Trim());
            return;
        }

        // Dự phòng 2: File cá nhân trong thư mục User
        var userFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deepseek_key.txt");
        if (File.Exists(userFile))
        {
            try
            {
                var lines = File.ReadAllLines(userFile)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#") && l.Length >= 20)
                    .Distinct()
                    .ToList();
                if (lines.Count > 0) _activeDeepSeekKeys.AddRange(lines);
            }
            catch { }
        }
    }

    private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (_activeApiKeys.Count == 0)
        {
            System.Windows.MessageBox.Show("Chưa có Gemini API Key. Hãy đặt file 'api_keys.txt' cạnh phần mềm (hoặc thiết lập biến môi trường GEMINI_API_KEY).", "Thiếu API Key", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await FetchModelsAsync(_activeApiKeys[0]);
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
        if (_activeApiKeys.Count == 0)
        {
            System.Windows.MessageBox.Show("Chưa có Gemini API Key. Hãy đặt file 'api_keys.txt' cạnh phần mềm (hoặc thiết lập biến môi trường GEMINI_API_KEY).", "Thiếu API Key", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_fileItems.Count == 0)
        {
            System.Windows.MessageBox.Show("Vui lòng thêm ít nhất một ảnh hoặc file PDF để chuyển đổi.", "Chưa chọn file", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_fileItems.Count > MaxAllowedImages)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"Bạn đang chọn {_fileItems.Count} ảnh/trang. Để đảm bảo AI bóc tách đầy đủ 100% không bị quá tải token hoặc dừng giữa chừng, khuyến nghị tối đa {MaxAllowedImages} ảnh/lần.\n\nBạn có muốn tiếp tục xử lý toàn bộ {_fileItems.Count} ảnh này không?",
                "Cảnh báo số lượng ảnh lớn",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
        }

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
        var liveModelsList = CmbModel.Items.Cast<object>().Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
        var filePathsSnapshot = _fileItems.Select(f => f.FilePath).ToList();
        int optionFormat = Math.Max(0, CmbOptionFormat.SelectedIndex); // 0: Tab, 1: Bảng ẩn viền, 2: Dấu cách
        int colorStyle = Math.Max(0, CmbColorStyle.SelectedIndex);     // 0: Màu như ảnh, 1: Đen trắng
        string? deepSeekKey = (_activeDeepSeekKeys.Count > 0) ? _activeDeepSeekKeys[0] : null;

        // Cập nhật trạng thái UI
        BtnConvert.IsEnabled = false;
        PrgBar.Visibility = Visibility.Visible;
        PrgBar.IsIndeterminate = false;
        PrgBar.Value = 10;
        TxtProgressPercent.Visibility = Visibility.Visible;
        TxtProgressPercent.Text = "10%";
        BusyBadge.Visibility = Visibility.Visible;
        TxtTitleBusy.Text = "Đang chuẩn bị (10%)...";
        TxtStatus.Text = "Đang tải và chuẩn bị dữ liệu hình ảnh...";

        var keysToSend = _activeApiKeys;
        var startKeyIdx = _keyRotationIndex % keysToSend.Count;

        // Timer giả lập tiến độ mượt mà từ 35% -> 85% trong lúc AI đang suy nghĩ
        var progressTimer = new System.Windows.Threading.DispatcherTimer();
        progressTimer.Interval = TimeSpan.FromMilliseconds(400);
        progressTimer.Tick += (s, e) =>
        {
            if (PrgBar.Value < 88)
            {
                PrgBar.Value += (PrgBar.Value < 60) ? 2 : 1;
                TxtProgressPercent.Text = $"{(int)PrgBar.Value}%";
            }
        };

        try
        {
            var htmlOutput = await Task.Run(async () =>
            {
                var images = GeminiOcrService.LoadImages(filePathsSnapshot);
                Dispatcher.Invoke(() =>
                {
                    PrgBar.Value = 15;
                    TxtProgressPercent.Text = "15%";
                    TxtTitleBusy.Text = "Đang nạp ảnh (15%)...";
                });

                return await GeminiOcrService.ConvertToWordHtmlAsync(
                    keysToSend,
                    images,
                    selectedModel,
                    progress => Dispatcher.Invoke(() =>
                    {
                        TxtStatus.Text = progress;
                        // Trích xuất số % thực tế nếu có trong chuỗi progress
                        var match = System.Text.RegularExpressions.Regex.Match(progress, @"\((\d+)%\)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
                        {
                            // Scale từ 15% đến 92%
                            int scaledValue = 15 + (int)(pct * 0.77);
                            PrgBar.Value = Math.Max(PrgBar.Value, scaledValue);
                            TxtProgressPercent.Text = $"{scaledValue}%";
                            TxtTitleBusy.Text = $"{progress}";
                        }
                        else
                        {
                            TxtTitleBusy.Text = $"{progress} ({(int)PrgBar.Value}%)";
                        }
                    }),
                    liveModelsList,
                    startKeyIdx,
                    optionFormat,
                    colorStyle,
                    deepSeekKey
                );
            });

            PrgBar.Value = 96;
            TxtProgressPercent.Text = "96%";

            // Sau mỗi lần bóc tách thành công, xoay vòng sang Key kế tiếp cho lần sau
            _keyRotationIndex = (startKeyIdx + 1) % keysToSend.Count;

            TxtStatus.Text = "Đang ghi file Word ra đĩa...";
            // Ghi file với UTF-8 có BOM (Byte Order Mark) để Microsoft Word mở tự động 100% không hiện bảng hỏi File Conversion
            await File.WriteAllTextAsync(outputPath, htmlOutput, new System.Text.UTF8Encoding(true));

            PrgBar.Value = 100;
            TxtProgressPercent.Text = "100%";
            TxtStatus.Text = $"Hoàn tất 100%: {Path.GetFileName(outputPath)}";
            PrgBar.Visibility = Visibility.Hidden;
            TxtProgressPercent.Visibility = Visibility.Collapsed;
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
            progressTimer?.Stop();
            BtnConvert.IsEnabled = true;
            PrgBar.Visibility = Visibility.Hidden;
            TxtProgressPercent.Visibility = Visibility.Collapsed;
            BusyBadge.Visibility = Visibility.Collapsed;
        }
    }
}