using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
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

    private static readonly string ThemeConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ocr_word_gemini_theme.txt"
    );

    private static readonly string BackgroundConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ocr_word_gemini_bg.txt"
    );

    private static readonly string BackgroundModeConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ocr_word_gemini_bg_mode.txt"
    );

    private static readonly string BackgroundTransformConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ocr_word_gemini_bg_transform.txt"
    );

    public enum BackgroundFitMode
    {
        FitAndBlur, // Vừa vặn + Blur hai bên
        Fill,       // Phủ kín toàn màn hình (Cover)
        Center,     // Kích thước gốc căn giữa
        Custom      // Tùy chỉnh góc (Kéo rê + Zoom)
    }

    private BackgroundFitMode _currentBgMode = BackgroundFitMode.FitAndBlur;
    private string? _currentBgPath;
    private bool _isBgPanModeActive = false;
    private bool _isDraggingBg = false;
    private Point _lastDragPoint;
    private double _customScale = 1.0;
    private double _customTranslateX = 0;
    private double _customTranslateY = 0;
    private System.Windows.Threading.DispatcherTimer? _saveTransformTimer;

    private readonly List<string> _activeApiKeys = new();
    private readonly List<string> _activeDeepSeekKeys = new();
    private int _keyRotationIndex = 0;
    private CancellationTokenSource? _conversionCts;
    private ResourceDictionary? _lightGlassDictionary;
    private ApplicationTheme _currentTheme = ApplicationTheme.Dark;

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

        // Lắng nghe sự kiện chuột khi bật chế độ Kéo rê / Chỉnh góc hình nền
        this.PreviewMouseWheel += MainWindow_PreviewMouseWheel;
        this.PreviewMouseDown += MainWindow_PreviewMouseDown;
        this.PreviewMouseMove += MainWindow_PreviewMouseMove;
        this.PreviewMouseUp += MainWindow_PreviewMouseUp;

        LoadSavedTheme();
        LoadSavedBackground();
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

    private void UpdateFileCountBadge()
    {
        if (TxtFileCountBadge != null)
        {
            TxtFileCountBadge.Text = $"{_fileItems.Count} ảnh đã chọn";
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
                FitImageToView();
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
                    FitImageToView();
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
            ResetPreviewZoom();
        }
    }

    #region Image Preview Zoom & Pan Controls
    private double _currentZoom = 1.0;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private bool _isPanning = false;
    private System.Windows.Point _panStartPoint;
    private double _startHOffset;
    private double _startVOffset;

    private void SetPreviewZoom(double zoom)
    {
        _currentZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (PreviewScaleTransform != null)
        {
            PreviewScaleTransform.ScaleX = _currentZoom;
            PreviewScaleTransform.ScaleY = _currentZoom;
        }
        if (TxtZoomPercent != null)
        {
            TxtZoomPercent.Text = $"{Math.Round(_currentZoom * 100)}%";
        }
    }

    private void ResetPreviewZoom()
    {
        SetPreviewZoom(1.0);
    }

    private void FitImageToView()
    {
        if (ImgPreview?.Source == null || ScrollPreview == null)
        {
            ResetPreviewZoom();
            return;
        }

        var viewportWidth = ScrollPreview.ActualWidth > 20 ? ScrollPreview.ActualWidth - 16 : 300;
        var viewportHeight = ScrollPreview.ActualHeight > 20 ? ScrollPreview.ActualHeight - 16 : 400;

        var imgWidth = ImgPreview.Source.Width;
        var imgHeight = ImgPreview.Source.Height;

        if (imgWidth <= 0 || imgHeight <= 0)
        {
            ResetPreviewZoom();
            return;
        }

        var scaleX = viewportWidth / imgWidth;
        var scaleY = viewportHeight / imgHeight;
        var fitScale = Math.Min(scaleX, scaleY);

        if (fitScale <= 0) fitScale = 1.0;
        SetPreviewZoom(fitScale);

        ScrollPreview.ScrollToHorizontalOffset(0);
        ScrollPreview.ScrollToVerticalOffset(0);
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(_currentZoom * 1.25);
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(_currentZoom / 1.25);
    }

    private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
    {
        FitImageToView();
    }

    private void ScrollPreview_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (ImgPreview?.Source == null) return;

        e.Handled = true;
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        SetPreviewZoom(_currentZoom * factor);
    }

    private void ImgPreviewContainer_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Nhấp đúp: Reset / Fit
            FitImageToView();
            return;
        }

        if (ImgPreview?.Source == null || ScrollPreview == null) return;

        _isPanning = true;
        _panStartPoint = e.GetPosition(ScrollPreview);
        _startHOffset = ScrollPreview.HorizontalOffset;
        _startVOffset = ScrollPreview.VerticalOffset;
        ImgPreviewContainer.CaptureMouse();
        ImgPreview.Cursor = System.Windows.Input.Cursors.SizeAll;
    }

    private void ImgPreviewContainer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning || ScrollPreview == null) return;

        var currentPoint = e.GetPosition(ScrollPreview);
        var deltaX = currentPoint.X - _panStartPoint.X;
        var deltaY = currentPoint.Y - _panStartPoint.Y;

        ScrollPreview.ScrollToHorizontalOffset(_startHOffset - deltaX);
        ScrollPreview.ScrollToVerticalOffset(_startVOffset - deltaY);
    }

    private void ImgPreviewContainer_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ImgPreviewContainer.ReleaseMouseCapture();
            ImgPreview.Cursor = System.Windows.Input.Cursors.Hand;
        }
    }

    private void ImgPreviewContainer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ImgPreviewContainer.ReleaseMouseCapture();
            ImgPreview.Cursor = System.Windows.Input.Cursors.Hand;
        }
    }
    #endregion

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

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _conversionCts?.Cancel();
        TxtStatus.Text = "Đang hủy chuyển đổi...";
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
        var deepSeekKeys = _activeDeepSeekKeys;

        // Thu thập các trang bị bỏ qua (khi cả DeepSeek lẫn Gemini đều thất bại)
        var skippedPages = new System.Collections.Concurrent.ConcurrentBag<int>();

        // Token để hủy giữa chừng
        _conversionCts?.Dispose();
        _conversionCts = new CancellationTokenSource();
        var conversionToken = _conversionCts.Token;

        // Cập nhật trạng thái UI
        BtnConvert.IsEnabled = false;
        BtnCancel.Visibility = Visibility.Visible;
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
                    deepSeekKeys,
                    skippedPages,
                    conversionToken
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

            _lastConvertedOutputPath = outputPath;
            ShowSuccessModal(outputPath, skippedPages);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Đã hủy chuyển đổi.";
            System.Windows.MessageBox.Show("Đã hủy chuyển đổi.", "Đã hủy", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
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
            BtnCancel.Visibility = Visibility.Collapsed;
            _conversionCts?.Dispose();
            _conversionCts = null;
            PrgBar.Visibility = Visibility.Hidden;
            TxtProgressPercent.Visibility = Visibility.Collapsed;
            BusyBadge.Visibility = Visibility.Collapsed;
        }
    }

    #region Liquid Glass Success Modal Dialog Controls
    private string? _lastConvertedOutputPath;

    private void ShowSuccessModal(string outputPath, System.Collections.Concurrent.ConcurrentBag<int> skippedPages)
    {
        if (SuccessModalOverlay == null) return;

        TxtModalOutputPath.Text = outputPath;

        if (skippedPages.Count > 0)
        {
            TxtModalSkipNote.Text = $"⚠️ Có {skippedPages.Count} trang bị bỏ qua: trang {string.Join(", ", skippedPages.OrderBy(x => x))}.";
            TxtModalSkipNote.Visibility = Visibility.Visible;
        }
        else
        {
            TxtModalSkipNote.Visibility = Visibility.Collapsed;
        }

        SuccessModalOverlay.Visibility = Visibility.Visible;
    }

    private void BtnModalOpenWord_Click(object sender, RoutedEventArgs e)
    {
        SuccessModalOverlay.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(_lastConvertedOutputPath) && File.Exists(_lastConvertedOutputPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _lastConvertedOutputPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Không thể tự mở file: {ex.Message}";
            }
        }
    }

    private void BtnModalClose_Click(object sender, RoutedEventArgs e)
    {
        SuccessModalOverlay.Visibility = Visibility.Collapsed;
    }
    #endregion

    private void LoadSavedTheme()
    {
        try
        {
            if (File.Exists(ThemeConfigFilePath))
            {
                var saved = File.ReadAllText(ThemeConfigFilePath).Trim();
                if (string.Equals(saved, "Light", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyTheme(ApplicationTheme.Light, save: false);
                    return;
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi đọc cấu hình theme
        }
        ApplyTheme(ApplicationTheme.Dark, save: false);
    }

    private void SaveTheme(ApplicationTheme theme)
    {
        try
        {
            File.WriteAllText(ThemeConfigFilePath, theme.ToString());
        }
        catch
        {
            // Bỏ qua lỗi ghi cấu hình theme
        }
    }

    private void ApplyTheme(ApplicationTheme theme, bool save = true)
    {
        _currentTheme = theme;

        try
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Acrylic, updateAccent: true);
        }
        catch
        {
            // Dự phòng nếu hệ điều hành không hỗ trợ Acrylic
            ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
        }

        var appMerged = System.Windows.Application.Current.Resources.MergedDictionaries;

        if (theme == ApplicationTheme.Light)
        {
            if (_lightGlassDictionary == null)
            {
                _lightGlassDictionary = new ResourceDictionary
                {
                    Source = new Uri("Themes/LiquidGlass.Light.xaml", UriKind.RelativeOrAbsolute)
                };
            }

            if (!appMerged.Contains(_lightGlassDictionary))
            {
                appMerged.Add(_lightGlassDictionary);
            }

            if (IconTheme != null) IconTheme.Symbol = SymbolRegular.WeatherSunny24;
            if (TxtThemeMode != null) TxtThemeMode.Text = "Light";

            // Ở chế độ Light: Không phủ lớp trắng đục làm mờ ảnh nền, giữ nguyên độ trong trẻo và rực rỡ của wallpaper
            if (CustomBgOverlay != null)
                CustomBgOverlay.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10000000"));
        }
        else
        {
            if (_lightGlassDictionary != null && appMerged.Contains(_lightGlassDictionary))
            {
                appMerged.Remove(_lightGlassDictionary);
            }

            if (IconTheme != null) IconTheme.Symbol = SymbolRegular.WeatherMoon24;
            if (TxtThemeMode != null) TxtThemeMode.Text = "Dark";

            // Ở chế độ Dark: Lớp phủ tối mỏng (#35000000) giúp chữ trắng nổi bật
            if (CustomBgOverlay != null)
                CustomBgOverlay.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#35000000"));
        }

        if (save)
        {
            SaveTheme(theme);
        }
    }

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var targetTheme = _currentTheme == ApplicationTheme.Dark 
            ? ApplicationTheme.Light 
            : ApplicationTheme.Dark;

        ApplyTheme(targetTheme, save: true);
    }

    private void LoadSavedBackground()
    {
        try
        {
            if (File.Exists(BackgroundTransformConfigFilePath))
            {
                var transStr = File.ReadAllText(BackgroundTransformConfigFilePath).Trim();
                var parts = transStr.Split(';');
                if (parts.Length == 3 &&
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var scale) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tx) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ty))
                {
                    _customScale = Math.Clamp(scale, 0.5, 5.0);
                    _customTranslateX = tx;
                    _customTranslateY = ty;
                }
            }

            if (File.Exists(BackgroundModeConfigFilePath))
            {
                var modeStr = File.ReadAllText(BackgroundModeConfigFilePath).Trim();
                if (Enum.TryParse<BackgroundFitMode>(modeStr, true, out var savedMode))
                {
                    _currentBgMode = savedMode;
                }
            }

            if (File.Exists(BackgroundConfigFilePath))
            {
                var bgPath = File.ReadAllText(BackgroundConfigFilePath).Trim();
                if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
                {
                    ApplyBackgroundImage(bgPath, save: false);
                    if (_currentBgMode == BackgroundFitMode.Custom)
                    {
                        // Khi khởi động lại ứng dụng, tự động khóa góc đã lưu để người dùng thao tác ứng dụng bình thường
                        SetBgPanMode(false);
                        UpdateSaveCustomBgButtonState(isLocked: true);
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi nạp ảnh nền đã lưu
        }
    }

    private void ApplyBackgroundImage(string? imagePath, bool save = true)
    {
        _currentBgPath = imagePath;

        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            ImgBlurBackground.Source = null;
            ImgBlurBackground.Visibility = Visibility.Collapsed;

            ImgCustomBackground.Source = null;
            ImgCustomBackground.Visibility = Visibility.Collapsed;

            CustomBgOverlay.Visibility = Visibility.Collapsed;
            BtnClearBackground.Visibility = Visibility.Collapsed;
            BtnBgFitMode.Visibility = Visibility.Collapsed;
            if (BtnSaveCustomBg != null) BtnSaveCustomBg.Visibility = Visibility.Collapsed;

            SetBgPanMode(false);

            if (save)
            {
                try
                {
                    if (File.Exists(BackgroundConfigFilePath))
                        File.Delete(BackgroundConfigFilePath);
                }
                catch { }
            }
            return;
        }

        try
        {
            // Đọc trực tiếp qua FileStream để nạp toàn bộ vào RAM, tránh lock file ảnh gốc
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();

            ImgBlurBackground.Source = bmp;
            ImgCustomBackground.Source = bmp;

            UpdateBackgroundLayout();

            CustomBgOverlay.Background = _currentTheme == ApplicationTheme.Light
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10000000"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#35000000"));

            CustomBgOverlay.Visibility = Visibility.Visible;
            BtnClearBackground.Visibility = Visibility.Visible;
            BtnBgFitMode.Visibility = Visibility.Visible;

            if (save)
            {
                File.WriteAllText(BackgroundConfigFilePath, imagePath);
                File.WriteAllText(BackgroundModeConfigFilePath, _currentBgMode.ToString());
                SaveBackgroundTransform();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Không thể nạp ảnh nền:\n{ex.Message}", "Lỗi ảnh nền", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void SaveBackgroundTransform(bool debounce = true)
    {
        if (debounce)
        {
            if (_saveTransformTimer == null)
            {
                _saveTransformTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _saveTransformTimer.Tick += (s, e) =>
                {
                    _saveTransformTimer.Stop();
                    SaveBackgroundTransform(debounce: false);
                };
            }
            _saveTransformTimer.Stop();
            _saveTransformTimer.Start();
            return;
        }

        try
        {
            var str = $"{_customScale.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)};{_customTranslateX.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)};{_customTranslateY.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}";
            File.WriteAllText(BackgroundTransformConfigFilePath, str);
        }
        catch { }
    }

    private void UpdateBackgroundLayout()
    {
        switch (_currentBgMode)
        {
            case BackgroundFitMode.FitAndBlur:
                // Lớp 1: Blur toàn màn hình phía sau xóa tan viền đen
                ImgBlurBackground.Visibility = Visibility.Visible;
                ImgBlurBackground.Stretch = System.Windows.Media.Stretch.UniformToFill;

                // Lớp 2: Ảnh chính vừa vặn không bị cắt xén
                ImgCustomBackground.Visibility = Visibility.Visible;
                ImgCustomBackground.Stretch = System.Windows.Media.Stretch.Uniform;
                ImgCustomBackground.HorizontalAlignment = HorizontalAlignment.Center;
                ImgCustomBackground.VerticalAlignment = VerticalAlignment.Center;

                BgScaleTransform.ScaleX = 1.0;
                BgScaleTransform.ScaleY = 1.0;
                BgTranslateTransform.X = 0;
                BgTranslateTransform.Y = 0;

                if (TxtBgFitMode != null) TxtBgFitMode.Text = "Vừa vặn";
                if (IconBgFitMode != null) IconBgFitMode.Symbol = SymbolRegular.SlideSize24;
                if (BtnSaveCustomBg != null) BtnSaveCustomBg.Visibility = Visibility.Collapsed;
                SetBgPanMode(false);
                break;

            case BackgroundFitMode.Fill:
                // Phủ kín toàn bộ (Cover)
                ImgBlurBackground.Visibility = Visibility.Collapsed;

                ImgCustomBackground.Visibility = Visibility.Visible;
                ImgCustomBackground.Stretch = System.Windows.Media.Stretch.UniformToFill;
                ImgCustomBackground.HorizontalAlignment = HorizontalAlignment.Center;
                ImgCustomBackground.VerticalAlignment = VerticalAlignment.Center;

                BgScaleTransform.ScaleX = 1.0;
                BgScaleTransform.ScaleY = 1.0;
                BgTranslateTransform.X = 0;
                BgTranslateTransform.Y = 0;

                if (TxtBgFitMode != null) TxtBgFitMode.Text = "Phủ đầy";
                if (IconBgFitMode != null) IconBgFitMode.Symbol = SymbolRegular.FullScreenMaximize24;
                if (BtnSaveCustomBg != null) BtnSaveCustomBg.Visibility = Visibility.Collapsed;
                SetBgPanMode(false);
                break;

            case BackgroundFitMode.Center:
                // Căn giữa kích thước gốc
                ImgBlurBackground.Visibility = Visibility.Visible;
                ImgBlurBackground.Stretch = System.Windows.Media.Stretch.UniformToFill;

                ImgCustomBackground.Visibility = Visibility.Visible;
                ImgCustomBackground.Stretch = System.Windows.Media.Stretch.None;
                ImgCustomBackground.HorizontalAlignment = HorizontalAlignment.Center;
                ImgCustomBackground.VerticalAlignment = VerticalAlignment.Center;

                BgScaleTransform.ScaleX = 1.0;
                BgScaleTransform.ScaleY = 1.0;
                BgTranslateTransform.X = 0;
                BgTranslateTransform.Y = 0;

                if (TxtBgFitMode != null) TxtBgFitMode.Text = "Căn giữa";
                if (IconBgFitMode != null) IconBgFitMode.Symbol = SymbolRegular.AlignCenterHorizontal24;
                if (BtnSaveCustomBg != null) BtnSaveCustomBg.Visibility = Visibility.Collapsed;
                SetBgPanMode(false);
                break;

            case BackgroundFitMode.Custom:
                // Tùy chỉnh góc tự do (Kéo rê + Zoom):
                ImgBlurBackground.Visibility = Visibility.Visible;
                ImgBlurBackground.Stretch = System.Windows.Media.Stretch.UniformToFill;

                ImgCustomBackground.Visibility = Visibility.Visible;
                ImgCustomBackground.Stretch = System.Windows.Media.Stretch.Uniform;
                ImgCustomBackground.HorizontalAlignment = HorizontalAlignment.Center;
                ImgCustomBackground.VerticalAlignment = VerticalAlignment.Center;

                BgScaleTransform.ScaleX = _customScale;
                BgScaleTransform.ScaleY = _customScale;
                BgTranslateTransform.X = _customTranslateX;
                BgTranslateTransform.Y = _customTranslateY;

                if (TxtBgFitMode != null) TxtBgFitMode.Text = "Tùy chỉnh góc";
                if (IconBgFitMode != null) IconBgFitMode.Symbol = SymbolRegular.Crop24;

                // Hiển thị nút Khóa góc & bật chế độ căn chỉnh ban đầu
                if (BtnSaveCustomBg != null)
                {
                    BtnSaveCustomBg.Visibility = Visibility.Visible;
                    UpdateSaveCustomBgButtonState(isLocked: false);
                }
                SetBgPanMode(true);
                break;
        }
    }

    private void BtnBgFitMode_Click(object sender, RoutedEventArgs e)
    {
        // Chuyển đổi xoay vòng giữa 4 chế độ: Vừa vặn -> Phủ đầy -> Căn giữa -> Tùy chỉnh góc
        _currentBgMode = _currentBgMode switch
        {
            BackgroundFitMode.FitAndBlur => BackgroundFitMode.Fill,
            BackgroundFitMode.Fill => BackgroundFitMode.Center,
            BackgroundFitMode.Center => BackgroundFitMode.Custom,
            _ => BackgroundFitMode.FitAndBlur
        };

        UpdateBackgroundLayout();

        try
        {
            File.WriteAllText(BackgroundModeConfigFilePath, _currentBgMode.ToString());
        }
        catch { }
    }

    private void SetBgPanMode(bool active)
    {
        _isBgPanModeActive = active;
        if (PanInteractiveOverlay != null)
        {
            PanInteractiveOverlay.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            PanInteractiveOverlay.IsHitTestVisible = active;
            PanInteractiveOverlay.Cursor = active ? Cursors.SizeAll : Cursors.Arrow;
        }

        if (active)
        {
            TxtStatus.Text = "🖐️ Chế độ Tùy chỉnh góc: Giữ chuột trái kéo để di chuyển ảnh, Lăn chuột để phóng to/thu nhỏ góc ảnh đẹp nhất.";
        }
    }

    private void PanOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDraggingBg = true;
            _lastDragPoint = e.GetPosition(this);
            PanInteractiveOverlay.CaptureMouse();
            e.Handled = true;
        }
    }

    private void PanOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingBg) return;

        var currentPoint = e.GetPosition(this);
        var deltaX = currentPoint.X - _lastDragPoint.X;
        var deltaY = currentPoint.Y - _lastDragPoint.Y;

        _lastDragPoint = currentPoint;

        _customTranslateX += deltaX;
        _customTranslateY += deltaY;

        BgTranslateTransform.X = _customTranslateX;
        BgTranslateTransform.Y = _customTranslateY;

        e.Handled = true;
    }

    private void PanOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingBg)
        {
            _isDraggingBg = false;
            PanInteractiveOverlay.ReleaseMouseCapture();
            SaveBackgroundTransform();
            e.Handled = true;
        }
    }

    private void PanOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAtPoint(e.GetPosition(this), e.Delta);
        e.Handled = true;
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isBgPanModeActive || e.LeftButton != MouseButtonState.Pressed) return;

        // Bỏ qua nếu bấm vào TitleBar hoặc các nút để người dùng vẫn thao tác được
        if (e.OriginalSource is DependencyObject dep && FindParent<Wpf.Ui.Controls.Button>(dep) != null)
            return;

        _isDraggingBg = true;
        _lastDragPoint = e.GetPosition(this);
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isBgPanModeActive || !_isDraggingBg) return;

        var currentPoint = e.GetPosition(this);
        var deltaX = currentPoint.X - _lastDragPoint.X;
        var deltaY = currentPoint.Y - _lastDragPoint.Y;

        _lastDragPoint = currentPoint;

        _customTranslateX += deltaX;
        _customTranslateY += deltaY;

        BgTranslateTransform.X = _customTranslateX;
        BgTranslateTransform.Y = _customTranslateY;

        e.Handled = true;
    }

    private void MainWindow_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingBg)
        {
            _isDraggingBg = false;
            Mouse.Capture(null);
            SaveBackgroundTransform();
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isBgPanModeActive) return;

        ZoomAtPoint(e.GetPosition(this), e.Delta);
        e.Handled = true;
    }

    private void ZoomAtPoint(Point mousePos, int delta)
    {
        double oldScale = _customScale;
        double zoomFactor = delta > 0 ? 1.12 : 0.89;
        double newScale = Math.Clamp(oldScale * zoomFactor, 0.4, 8.0);

        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        // Vị trí con trỏ chuột so với ImgCustomBackground trước khi áp dụng RenderTransform:
        // Do RenderTransformOrigin="0,0", vị trí của điểm trên ảnh sau transform là:
        // ScreenPos = ImgBasePos + CustomTranslate + ImgPoint * Scale
        // => ImgPoint * oldScale = mousePos - ImgBasePos - oldTranslate
        // Để giữ điểm ImgPoint cố định tại mousePos sau khi scale thành newScale:
        // mousePos = ImgBasePos + newTranslate + ImgPoint * newScale
        // => newTranslate = mousePos - ImgBasePos - (mousePos - ImgBasePos - oldTranslate) * (newScale / oldScale)
        // Đặt P = mousePos - ImgBasePos:
        // newTranslate = P - (P - oldTranslate) * (newScale / oldScale)

        Point imgBasePos;
        try
        {
            // Lấy vị trí góc trên-trái (0,0) chưa biến đổi của ImgCustomBackground trên cửa sổ
            var baseTransform = ImgCustomBackground.TransformToAncestor(this);
            // Trừ đi translate hiện tại để tìm gốc layout thực sự
            var currentTransformedTopLeft = baseTransform.Transform(new Point(0, 0));
            imgBasePos = new Point(currentTransformedTopLeft.X - _customTranslateX, currentTransformedTopLeft.Y - _customTranslateY);
        }
        catch
        {
            // Fallback nếu chưa layout xong: giả định căn giữa
            double fallbackX = Math.Max(0, (this.ActualWidth - ImgCustomBackground.ActualWidth) / 2.0);
            double fallbackY = Math.Max(0, (this.ActualHeight - ImgCustomBackground.ActualHeight) / 2.0);
            imgBasePos = new Point(fallbackX, fallbackY);
        }

        double pX = mousePos.X - imgBasePos.X;
        double pY = mousePos.Y - imgBasePos.Y;
        double scaleRatio = newScale / oldScale;

        _customTranslateX = pX - (pX - _customTranslateX) * scaleRatio;
        _customTranslateY = pY - (pY - _customTranslateY) * scaleRatio;
        _customScale = newScale;

        BgScaleTransform.ScaleX = _customScale;
        BgScaleTransform.ScaleY = _customScale;
        BgTranslateTransform.X = _customTranslateX;
        BgTranslateTransform.Y = _customTranslateY;

        SaveBackgroundTransform();
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }

    private void BtnChangeBackground_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Chọn ảnh nền cho giao diện (Background)",
            Filter = "Tập tin hình ảnh (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Tất cả tập tin (*.*)|*.*",
            Multiselect = false
        };

        if (ofd.ShowDialog() == true)
        {
            ApplyBackgroundImage(ofd.FileName, save: true);
        }
    }

    private void UpdateSaveCustomBgButtonState(bool isLocked)
    {
        if (BtnSaveCustomBg == null) return;

        if (isLocked)
        {
            // Trạng thái: Đã khóa (Click vào để mở lại chế độ chỉnh)
            BtnSaveCustomBg.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1538BDF8"));
            BtnSaveCustomBg.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5038BDF8"));
            if (IconSaveCustomBg != null)
            {
                IconSaveCustomBg.Symbol = SymbolRegular.LockClosed24;
                IconSaveCustomBg.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#38BDF8"));
            }
            if (TxtSaveCustomBg != null)
            {
                TxtSaveCustomBg.Text = "Đã khóa (Bấm để chỉnh)";
                TxtSaveCustomBg.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#38BDF8"));
            }
            BtnSaveCustomBg.ToolTip = "Góc ảnh đã được cố định an toàn. Bấm vào đây nếu muốn mở khóa để căn chỉnh lại.";
        }
        else
        {
            // Trạng thái: Đang chỉnh (Click vào để Lưu và Khóa)
            BtnSaveCustomBg.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2510B981"));
            BtnSaveCustomBg.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7010B981"));
            if (IconSaveCustomBg != null)
            {
                IconSaveCustomBg.Symbol = SymbolRegular.Checkmark24;
                IconSaveCustomBg.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#34D399"));
            }
            if (TxtSaveCustomBg != null)
            {
                TxtSaveCustomBg.Text = "Lưu & Khóa góc";
                TxtSaveCustomBg.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#34D399"));
            }
            BtnSaveCustomBg.ToolTip = "Bấm để lưu và khóa vị trí góc ảnh hiện tại, trả lại thao tác chuột bình thường cho phần mềm.";
        }
    }

    private void BtnSaveCustomBg_Click(object sender, RoutedEventArgs e)
    {
        if (_isBgPanModeActive)
        {
            // Đang chỉnh -> Khóa lại và lưu
            SetBgPanMode(false);
            SaveBackgroundTransform(debounce: false);
            UpdateSaveCustomBgButtonState(isLocked: true);
            TxtStatus.Text = "🔒 Đã lưu & khóa góc hình nền. Giờ bạn có thể thoải mái thao tác với tài liệu và danh sách ảnh!";
        }
        else
        {
            // Đang khóa -> Mở khóa để chỉnh tiếp
            SetBgPanMode(true);
            UpdateSaveCustomBgButtonState(isLocked: false);
            TxtStatus.Text = "🖐️ Đã mở khóa: Giữ chuột trái kéo hoặc lăn chuột zoom góc ảnh theo ý muốn, xong bấm 'Lưu & Khóa góc'.";
        }
    }

    private void BtnClearBackground_Click(object sender, RoutedEventArgs e)
    {
        SetBgPanMode(false);
        ApplyBackgroundImage(null, save: true);
    }
}