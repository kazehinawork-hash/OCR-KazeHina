using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace OcrWordApp;

public class GeminiOcrService
{
    private static readonly HttpClient HttpClient = new HttpClient();

    public record ImagePayload(string MimeType, string Base64Data);

    public static List<ImagePayload> LoadImages(IEnumerable<string> filePaths)
    {
        var result = new List<ImagePayload>();

        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf")
            {
                bool hasExtractedImages = false;
                try
                {
                    using var pdf = PdfDocument.Open(path);
                    foreach (var page in pdf.GetPages())
                    {
                        var images = page.GetImages();
                        foreach (var img in images)
                        {
                            if (img.TryGetPng(out var bytes))
                            {
                                result.Add(new ImagePayload("image/png", Convert.ToBase64String(bytes)));
                                hasExtractedImages = true;
                            }
                            else
                            {
                                var rawBytes = img.RawBytes.ToArray();
                                result.Add(new ImagePayload("image/png", Convert.ToBase64String(rawBytes)));
                                hasExtractedImages = true;
                            }
                        }
                    }
                }
                catch
                {
                    // Nếu PdfPig gặp lỗi font hoặc định dạng, fallback gửi trực tiếp PDF
                    hasExtractedImages = false;
                }

                // Nếu là file PDF kỹ thuật số (không chứa ảnh nhúng hoặc trang văn bản/vector),
                // gửi trực tiếp toàn bộ tài liệu PDF (application/pdf) đến Gemini API để bóc tách toàn vẹn!
                if (!hasExtractedImages)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    result.Add(new ImagePayload("application/pdf", Convert.ToBase64String(ms.ToArray())));
                }
            }
            else if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tif" or ".tiff")
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                var bytes = ms.ToArray();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg"
                };
                result.Add(new ImagePayload(mime, Convert.ToBase64String(bytes)));
            }
        }

        return result;
    }

    public static Task<string> ConvertToWordHtmlAsync(
        string apiKey,
        List<ImagePayload> images,
        string modelName = "gemini-3.1-flash-lite",
        Action<string>? onProgress = null,
        List<string>? availableModels = null,
        int optionFormat = 0,
        int colorStyle = 0,
        IReadOnlyList<string>? deepSeekKeys = null,
        CancellationToken cancellationToken = default)
    {
        return ConvertToWordHtmlAsync(new[] { apiKey }, images, modelName, onProgress, availableModels, 0, optionFormat, colorStyle, deepSeekKeys, null, cancellationToken);
    }

    public static async Task<string> ConvertToWordHtmlAsync(
        IReadOnlyList<string> apiKeys,
        List<ImagePayload> images,
        string modelName = "gemini-3.1-flash-lite",
        Action<string>? onProgress = null,
        List<string>? availableModels = null,
        int currentKeyIndex = 0,
        int optionFormat = 0,
        int colorStyle = 0,
        IReadOnlyList<string>? deepSeekKeys = null,
        System.Collections.Concurrent.ConcurrentBag<int>? skippedPages = null,
        CancellationToken cancellationToken = default)
    {
        if (apiKeys == null || apiKeys.Count == 0)
        {
            throw new InvalidOperationException("Chưa có API Key nào được cung cấp.");
        }

        if (images.Count == 0)
        {
            throw new InvalidOperationException("Không có hình ảnh nào được chọn để xử lý.");
        }

        // BÓC TÁCH SONG SONG CÓ GIỚI HẠN (BOUNDED PARALLEL PER-PAGE OCR):
        // Mỗi trang vẫn là 1 request riêng (giữ đúng thứ tự khi ghép), nhưng chạy song song tối đa vài trang
        // để tăng tốc. Mỗi trang bắt đầu ở một Key khác nhau để dàn tải, tránh dồn hết vào 1 key.
        var pageBodiesByIndex = new string?[images.Count];
        int initialKeyIndex = Math.Clamp(currentKeyIndex, 0, apiKeys.Count - 1);
        int maxConcurrency = Math.Max(1, Math.Min(3, images.Count));
        int completedCount = 0;
        using var throttler = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var pageTasks = new List<Task>(images.Count);
        for (int i = 0; i < images.Count; i++)
        {
            int index = i;
            await throttler.WaitAsync(cancellationToken);
            pageTasks.Add(Task.Run(async () =>
            {
                try
                {
                    int pageNum = index + 1;
                    int totalPages = images.Count;
                    int startKeyForPage = apiKeys.Count > 0 ? (initialKeyIndex + index) % apiKeys.Count : 0;

                    onProgress?.Invoke($"Đang bóc tách trang {pageNum}/{totalPages}...");

                    // DeepSeek là ưu tiên 1; Gemini dự phòng; nếu cả hai hỏng thì BỎ QUA trang và tiếp tục.
                    var pageHtml = await TryProcessPageAsync(
                        deepSeekKeys,
                        apiKeys,
                        images[index],
                        pageNum,
                        totalPages,
                        modelName,
                        startKeyForPage,
                        availableModels,
                        optionFormat,
                        colorStyle,
                        skippedPages,
                        onProgress,
                        cancellationToken);

                    string bodyFragment = (pageHtml != null)
                        ? ExtractInnerBodyContent(pageHtml)
                        : BuildFailedPageNote(pageNum);

                    if (!string.IsNullOrWhiteSpace(bodyFragment))
                    {
                        pageBodiesByIndex[index] = bodyFragment;
                    }

                    int done = Interlocked.Increment(ref completedCount);
                    int pct = (int)Math.Round((double)done / totalPages * 100);
                    onProgress?.Invoke($"Đã hoàn tất trang {pageNum}/{totalPages} ({pct}%)...");
                }
                finally
                {
                    throttler.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(pageTasks);

        var pageBodies = new List<string>();
        foreach (var body in pageBodiesByIndex)
        {
            if (!string.IsNullOrWhiteSpace(body)) pageBodies.Add(body);
        }

        onProgress?.Invoke("Đang hợp nhất các trang vào tài liệu Microsoft Word hoàn chỉnh...");
        return AssembleMasterWordDocument(pageBodies, optionFormat);
    }

    /// <summary>
    /// Xử lý 1 trang: ưu tiên DeepSeek (xoay nhiều key), dự phòng Gemini (xoay model/key).
    /// Nếu cả hai đều thất bại -> trả về null (đánh dấu trang bị bỏ qua và tiếp tục).
    /// </summary>
    private static async Task<string?> TryProcessPageAsync(
        IReadOnlyList<string>? deepSeekKeys,
        IReadOnlyList<string> apiKeys,
        ImagePayload image,
        int pageNum,
        int totalPages,
        string modelName,
        int startKeyForPage,
        List<string>? availableModels,
        int optionFormat,
        int colorStyle,
        System.Collections.Concurrent.ConcurrentBag<int>? skippedPages,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (deepSeekKeys != null && deepSeekKeys.Count > 0)
        {
            try
            {
                return await DeepSeekOcrService.ConvertPageAsync(
                    deepSeekKeys,
                    image,
                    PromptConstants.GetSystemPrompt(optionFormat, colorStyle),
                    "deepseek-flash",
                    pageNum,
                    totalPages,
                    onProgress,
                    cancellationToken);
            }
            catch (Exception dsEx) when (dsEx is not OperationCanceledException)
            {
                onProgress?.Invoke($"Trang {pageNum}: DeepSeek thất bại, chuyển sang Gemini dự phòng...");
                try
                {
                    var (html, _, _) = await ProcessSinglePageWithRetryAsync(
                        apiKeys, image, pageNum, totalPages, modelName, startKeyForPage,
                        availableModels, optionFormat, colorStyle, onProgress, cancellationToken);
                    return html;
                }
                catch (Exception geminiEx) when (geminiEx is not OperationCanceledException)
                {
                    SkipPage(skippedPages, pageNum, onProgress);
                    return null;
                }
            }
        }

        try
        {
            var (html, _, _) = await ProcessSinglePageWithRetryAsync(
                apiKeys, image, pageNum, totalPages, modelName, startKeyForPage,
                availableModels, optionFormat, colorStyle, onProgress, cancellationToken);
            return html;
        }
        catch (Exception geminiEx) when (geminiEx is not OperationCanceledException)
        {
            SkipPage(skippedPages, pageNum, onProgress);
            return null;
        }
    }

    private static void SkipPage(System.Collections.Concurrent.ConcurrentBag<int>? skippedPages, int pageNum, Action<string>? onProgress)
    {
        skippedPages?.Add(pageNum);
        onProgress?.Invoke($"Trang {pageNum}: cả hai API đều thất bại -> bỏ qua trang này, tiếp tục các trang còn lại...");
    }

    private static string BuildFailedPageNote(int pageNum)
    {
        return $"<div class=\"avoid-break\" style=\"margin: 6pt 0;\"><p style=\"text-align: left; color: #b91c1c; font-style: italic;\">[Trang {pageNum}: không bóc tách được (bị chặn hoặc lỗi API) — vui lòng xử lý lại trang này.]</p></div>";
    }

    /// <summary>
    /// Xử lý độc lập 1 trang ảnh với đầy đủ cơ chế xoay Model trước -> xoay Key sau nếu gặp 429/503/RECITATION.
    /// </summary>
    private static async Task<(string htmlResult, int nextKeyIndex, string nextModel)> ProcessSinglePageWithRetryAsync(
        IReadOnlyList<string> apiKeys,
        ImagePayload image,
        int pageNum,
        int totalPages,
        string modelName,
        int keyIndex,
        List<string>? availableModels,
        int optionFormat,
        int colorStyle,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        int curKeyIdx = Math.Clamp(keyIndex, 0, apiKeys.Count - 1);
        string curModel = modelName;

        // Giới hạn tổng số lần thử (xoay model + xoay key) để KHÔNG bị treo vô hạn
        // khi toàn bộ mô hình và API Key đều đang bận (503) hoặc quá hạn mức (429).
        int modelPoolSize = (availableModels != null && availableModels.Count > 0) ? availableModels.Count : 6;
        int maxAttempts = Math.Max(1, apiKeys.Count) * (modelPoolSize + 1) + 4;
        int attemptCount = 0;
        int recitationCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            attemptCount++;
            if (attemptCount > maxAttempts)
            {
                var lyDo = (recitationCount > 0)
                    ? $"Bộ lọc bản quyền (RECITATION) của Gemini kích hoạt liên tục trên Trang {pageNum} sau {attemptCount} lần thử."
                    : $"Gemini liên tục báo bận (500/502/503/504) hoặc quá hạn mức (429) trên Trang {pageNum} sau {attemptCount} lần thử.";
                throw new Exception(
                    $"{lyDo}\n\n" +
                    "Gợi ý: chờ vài phút rồi bấm 'Bắt đầu' thử lại; hoặc xử lý riêng trang này; hoặc dùng API Key khác.");
            }
            var curApiKey = apiKeys[curKeyIdx];
            var keyBadge = (apiKeys.Count > 1) ? $" [Key {curKeyIdx + 1}/{apiKeys.Count}]" : "";

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{curModel}:generateContent?key={curApiKey}";

            var partsList = new List<object>
            {
                new { text = PromptConstants.GetSystemPrompt(optionFormat, colorStyle) },
                new { text = $"\n[ĐÂY LÀ TRANG {pageNum}/{totalPages} CỦA BỘ ĐỀ THI. BẮT BUỘC BÓC TÁCH ĐẦY ĐỦ 100% TẤT CẢ CÁC CÂU CỦA TRANG NÀY, KHÔNG ĐƯỢC BỎ SÓT BẤT KỲ CÂU NÀO!]" },
                new
                {
                    inline_data = new
                    {
                        mime_type = image.MimeType,
                        data = image.Base64Data
                    }
                }
            };

            var requestBody = new
            {
                contents = new[] { new { parts = partsList } },
                generationConfig = new
                {
                    // Giữ nhiệt độ THẤP để bảo toàn độ chính xác khi bóc tách NGUYÊN VĂN (không rút gọn, không diễn giải)
                    temperature = 0.2,
                    maxOutputTokens = 65536
                },
                safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
                }
            };

            HttpResponseMessage? response = null;
            string responseString = "";

            try
            {
                using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };

                response = await HttpClient.SendAsync(reqMsg, cancellationToken);
                responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Thử đổi sang model khác nếu lỗi kết nối mạng
                var fallback = GetNextFallbackModel(curModel, availableModels);
                if (!string.IsNullOrEmpty(fallback) && !fallback.Equals(curModel, StringComparison.OrdinalIgnoreCase))
                {
                    curModel = fallback;
                    continue;
                }
                throw;
            }

            if (response != null && response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;
                string generatedText = "";
                string finishReason = "";

                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var firstCand = candidates[0];
                    if (firstCand.TryGetProperty("finishReason", out var frProp))
                    {
                        finishReason = frProp.GetString() ?? "";
                    }

                    if (firstCand.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts))
                    {
                        var sb = new StringBuilder();
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var textProp))
                            {
                                sb.Append(textProp.GetString() ?? "");
                            }
                        }
                        generatedText = sb.ToString();
                    }
                }

                // Bộ lọc RECITATION (trùng bản quyền) -> KHÔNG báo lỗi ngay, KHÔNG rút gọn/diễn giải nội dung.
                // Chiến lược: thử lại NGUYÊN VĂN với model khác -> key khác -> chờ ngắn rồi thử lại.
                // Tất cả nằm trong giới hạn attemptCount nên không lặp vô hạn.
                if (string.IsNullOrWhiteSpace(generatedText) && finishReason.Equals("RECITATION", StringComparison.OrdinalIgnoreCase))
                {
                    recitationCount++;

                    var nextModelFallback = GetNextFallbackModel(curModel, availableModels);
                    if (!string.IsNullOrEmpty(nextModelFallback) && !nextModelFallback.Equals(curModel, StringComparison.OrdinalIgnoreCase))
                    {
                        onProgress?.Invoke($"Trang {pageNum}: Bộ lọc bản quyền kích hoạt, thử lại nguyên văn với mô hình '{nextModelFallback}' (lần {recitationCount})...");
                        curModel = nextModelFallback;
                        continue;
                    }

                    if (apiKeys.Count > 1 && curKeyIdx + 1 < apiKeys.Count)
                    {
                        curKeyIdx++;
                        curModel = (availableModels != null && availableModels.Count > 0) ? availableModels[0] : "gemini-3.1-flash-lite";
                        onProgress?.Invoke($"Trang {pageNum}: Bộ lọc bản quyền kích hoạt, thử lại với Key {curKeyIdx + 1}/{apiKeys.Count} (lần {recitationCount})...");
                        continue;
                    }

                    onProgress?.Invoke($"Trang {pageNum}: Bộ lọc bản quyền kích hoạt, chờ 2s rồi thử lại nguyên văn (lần {recitationCount})...");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(generatedText))
                {
                    return (CleanHtmlOutput(generatedText), curKeyIdx, curModel);
                }
            }

            // Chỉ những mã lỗi CÓ THỂ THỬ LẠI mới tiếp tục xoay model/key:
            // 404 (model bị khai tử), 429 (hết hạn mức), 500/502/503/504 (lỗi máy chủ/bận tạm thời).
            int statusCode = response != null ? (int)response.StatusCode : 500;

            bool isRetryable = statusCode == 404 || statusCode == 429 || statusCode == 500
                || statusCode == 502 || statusCode == 503 || statusCode == 504;

            if (isRetryable)
            {
                // ƯU TIÊN 1: XOAY MODEL TRƯỚC (gemini-3.1-flash-lite -> gemini-3.6-flash -> gemini-3.5-flash-lite...)
                var nextModelFallback = GetNextFallbackModel(curModel, availableModels);
                if (!string.IsNullOrEmpty(nextModelFallback) && !nextModelFallback.Equals(curModel, StringComparison.OrdinalIgnoreCase))
                {
                    var reason = (statusCode == 429) ? "vượt hạn mức"
                        : (statusCode == 404) ? "không tồn tại"
                        : "lỗi máy chủ";
                    onProgress?.Invoke($"Trang {pageNum}: Mô hình '{curModel}' {reason}, đổi sang '{nextModelFallback}'{keyBadge} (lần thử {attemptCount})...");
                    curModel = nextModelFallback;
                    continue;
                }

                // ƯU TIÊN 2: NẾU ĐÃ THỬ HẾT CÁC MODEL MÀ VẪN LỖI -> XOAY SANG KEY TIẾP THEO
                if (apiKeys.Count > 1 && curKeyIdx + 1 < apiKeys.Count)
                {
                    curKeyIdx++;
                    var defaultFirstModel = (availableModels != null && availableModels.Count > 0) ? availableModels[0] : "gemini-3.1-flash-lite";
                    curModel = defaultFirstModel;
                    onProgress?.Invoke($"Trang {pageNum}: Chuyển sang Key {curKeyIdx + 1}/{apiKeys.Count}...");
                    continue;
                }

                // Chờ ngắn rồi thử lại nếu hết key
                onProgress?.Invoke($"Trang {pageNum}: Giới hạn yêu cầu ({statusCode}), tự động thử lại sau 3s...");
                await Task.Delay(3000, cancellationToken);
                continue;
            }

            throw new Exception($"Lỗi từ Gemini API ở Trang {pageNum} ({statusCode}):\n{responseString}");
        }
    }

    /// <summary>
    /// Trích xuất nội dung HTML bên trong khối Section1 hoặc thẻ body để ghép nhiều trang.
    /// </summary>
    public static string ExtractInnerBodyContent(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // Ưu tiên lấy bên trong <div class="Section1">...</div>
        var sectionMatch = Regex.Match(html, @"<div[^>]*class=[""']Section1[""'][^>]*>(.*?)</div>\s*</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (sectionMatch.Success)
        {
            return sectionMatch.Groups[1].Value.Trim();
        }

        // Lấy bên trong <body>...</body>
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (bodyMatch.Success)
        {
            return bodyMatch.Groups[1].Value.Trim();
        }

        // Nếu chỉ là đoạn HTML thô
        return html.Trim();
    }

    /// <summary>
    /// Đóng gói các nội dung của từng trang vào đúng định dạng khung chuẩn Word A4 thống nhất.
    /// </summary>
    public static string AssembleMasterWordDocument(List<string> pageBodies, int optionFormat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:w=\"urn:schemas-microsoft-com:office:word\" xmlns=\"http://www.w3.org/TR/REC-html40\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<!--[if gte mso 9]><xml><w:WordDocument><w:View>Print</w:View><w:Zoom>100</w:Zoom><w:DoNotOptimizeForBrowser/></w:WordDocument></xml><![endif]-->");
        sb.AppendLine("<style>");
        sb.AppendLine("  @page Section1 { size: 595.3pt 841.9pt; margin: 2.0cm 2.0cm 2.0cm 2.0cm; mso-header-margin: 36pt; mso-footer-margin: 36pt; }");
        sb.AppendLine("  div.Section1 { page: Section1; }");
        sb.AppendLine("  .avoid-break { page-break-inside: avoid; mso-pagination: widow-orphan; }");
        sb.AppendLine("  body { font-family: \"Times New Roman\", Times, serif; font-size: 12pt; line-height: 1.25; color: #000000; text-align: left; }");
        sb.AppendLine("  p { margin: 0 0 3pt 0; text-align: left; }");
        sb.AppendLine("  td, div { text-align: left; }");
        sb.AppendLine("  math { font-family: \"Cambria Math\", serif; font-size: 14pt; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"Section1\">");

        for (int i = 0; i < pageBodies.Count; i++)
        {
            sb.AppendLine(pageBodies[i]);
            // Giữa các trang thêm khoảng cách phân cách câu thoáng đẹp
            if (i < pageBodies.Count - 1)
            {
                sb.AppendLine("<p style=\"margin: 6pt 0;\"></p>");
            }
        }

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    public static async Task<List<string>> GetAvailableModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new List<string>();

        var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Không thể tải danh sách mô hình ({response.StatusCode}): {err}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var models = new List<string>();

        if (doc.RootElement.TryGetProperty("models", out var modelsArr))
        {
            foreach (var item in modelsArr.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString() ?? "";
                    if (name.StartsWith("models/"))
                    {
                        name = name.Substring("models/".Length);
                    }

                    // Kiểm tra xem model có hỗ trợ generateContent không
                    bool canGenerate = false;
                    if (item.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        foreach (var m in methods.EnumerateArray())
                        {
                            if (m.GetString() == "generateContent")
                            {
                                canGenerate = true;
                                break;
                            }
                        }
                    }

                    // Chỉ lấy các model Gemini đa phương thức (Vision) chính thức, còn hoạt động:
                    // Bỏ qua các model chuyên code, tts, embedding, tuned, robot hoặc các bản deprecated / khai tử
                    if (canGenerate && name.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        var lower = name.ToLowerInvariant();

                        // Loại bỏ các model đời cũ đã bị Google khai tử (trả về lỗi 404):
                        if (lower.StartsWith("gemini-1.") || lower.StartsWith("gemini-2.5"))
                        {
                            continue;
                        }

                        // Loại bỏ các model không dùng được cho Vision OCR:
                        if (lower.Contains("embedding") ||
                            lower.Contains("aqa") ||
                            lower.Contains("imagen") ||
                            lower.Contains("image") ||
                            lower.Contains("robot") ||
                            lower.Contains("tuning") ||
                            lower.Contains("tuned") ||
                            lower.Contains("thinking") ||
                            lower.Contains("custom") ||
                            lower.Contains("tts") ||
                            lower.Contains("transcribe") ||
                            lower.Contains("banana"))
                        {
                            continue;
                        }

                        // Chỉ giữ lại các mô hình đa dụng (flash hoặc pro)
                        if (lower.Contains("flash") || lower.Contains("pro"))
                        {
                            models.Add(name);
                        }
                    }
                }
            }
        }

        // Rút gọn danh sách: Ưu tiên các mô hình mạnh, nhanh và ổn định nhất đang hoạt động
        var curatedModels = new List<string>();

        // 1. Danh sách ưu tiên hàng đầu các model ĐANG HOẠT ĐỘNG (ALIVE):
        string[] priorityList = {
            "gemini-3.1-flash-lite",
            "gemini-3.6-flash",
            "gemini-3.5-flash-lite",
            "gemini-3.7-flash",
            "gemini-3.8-flash",
            "gemini-flash-latest"
        };

        foreach (var p in priorityList)
        {
            if (models.Any(m => m.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                curatedModels.Add(p);
            }
        }

        // 2. Thêm các model khác nếu chưa có trong danh sách nhưng bỏ qua các bản đuôi ngày tháng dài dòng
        foreach (var m in models)
        {
            if (!curatedModels.Contains(m) && !Regex.IsMatch(m, @"-\d{8}"))
            {
                curatedModels.Add(m);
            }
        }

        // Nếu vì lý do API chưa trả về, giữ danh sách an toàn mặc định các model đang chạy
        if (curatedModels.Count == 0)
        {
            curatedModels.AddRange(new[] { "gemini-3.1-flash-lite", "gemini-2.5-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite" });
        }

        // Giới hạn danh sách tối đa 6 mô hình tốt nhất để giao diện gọn gàng
        return curatedModels.Take(6).ToList();
    }

    public static string CleanHtmlOutput(string rawText)
    {
        var text = rawText.Trim();

        // 1. Loại bỏ các trích dẫn [1], [2], [10]...
        text = Regex.Replace(text, @"\[\d+\]", "");

        // 2. Loại bỏ dấu bọc markdown ```html ... ``` nếu có
        if (text.StartsWith("```html", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(7);
        }
        else if (text.StartsWith("```"))
        {
            text = text.Substring(3);
        }

        var result = text.Trim();

        // Đảm bảo có thẻ <meta charset="utf-8"> để Microsoft Word mở tự động không bao giờ hiện bảng hỏi Encoding
        if (!result.Contains("charset=\"utf-8\"", StringComparison.OrdinalIgnoreCase) &&
            !result.Contains("charset=utf-8", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Contains("<head>", StringComparison.OrdinalIgnoreCase))
            {
                result = Regex.Replace(result, "(<head[^>]*>)", "$1\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\n<meta charset=\"utf-8\">", RegexOptions.IgnoreCase);
            }
            else if (result.Contains("<html>", StringComparison.OrdinalIgnoreCase))
            {
                result = Regex.Replace(result, "(<html[^>]*>)", "$1\n<head>\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\n<meta charset=\"utf-8\">\n</head>", RegexOptions.IgnoreCase);
            }
            else
            {
                result = "<!DOCTYPE html>\n<html>\n<head>\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\n<meta charset=\"utf-8\">\n</head>\n<body>\n" + result + "\n</body>\n</html>";
            }
        }

        return result;
    }

    /// <summary>
    /// Tìm mô hình dự phòng tiếp theo theo chuỗi ưu tiên từ danh sách các mô hình ĐANG CÒN SỐNG từ API Key.
    /// Giúp người dùng trải nghiệm liền mạch mà không chuyển sang các mô hình đã chết hoặc bị khai tử.
    /// </summary>
    public static string GetNextFallbackModel(string currentModel, List<string>? availableModels = null)
    {
        // Chuỗi ưu tiên các mô hình Vision OCR ổn định nhất đang hoạt động (ALIVE)
        string[] defaultPriorityChain = {
            "gemini-3.1-flash-lite",
            "gemini-3.6-flash",
            "gemini-3.5-flash-lite",
            "gemini-3.7-flash",
            "gemini-3.8-flash",
            "gemini-flash-latest"
        };

        var cur = currentModel.Trim().ToLowerInvariant();

        // 1. Nếu có danh sách model ĐANG SỐNG (availableModels):
        if (availableModels != null && availableModels.Count > 0)
        {
            // Sắp xếp các model đang sống theo thứ tự ưu tiên
            var liveCandidates = new List<string>();
            foreach (var p in defaultPriorityChain)
            {
                var match = availableModels.FirstOrDefault(m => m.Equals(p, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match) && !liveCandidates.Contains(match))
                {
                    liveCandidates.Add(match);
                }
            }
            // Thêm các model đang sống còn lại vào đuôi danh sách
            foreach (var m in availableModels)
            {
                if (!liveCandidates.Contains(m))
                {
                    liveCandidates.Add(m);
                }
            }

            // Tìm model tiếp theo sau model hiện tại trong danh sách ĐANG SỐNG
            int idx = liveCandidates.FindIndex(m => m.Equals(cur, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                for (int i = idx + 1; i < liveCandidates.Count; i++)
                {
                    if (!liveCandidates[i].Equals(cur, StringComparison.OrdinalIgnoreCase))
                    {
                        return liveCandidates[i];
                    }
                }

                // Đã duyệt hết danh sách model đang sống -> báo HẾT model dự phòng.
                // TUYỆT ĐỐI KHÔNG quay vòng lại model đầu để tránh lặp vô hạn khi mọi model đều bận/quá hạn mức.
                return "";
            }

            // Model hiện tại không nằm trong danh sách đang sống -> nhảy vào model đang sống đầu tiên
            var firstLive = liveCandidates.FirstOrDefault(m => !m.Equals(cur, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(firstLive)) return firstLive;

            return "";
        }

        // 2. Dự phòng khi chưa có danh sách availableModels: Dùng danh sách mặc định
        int defaultIdx = Array.FindIndex(defaultPriorityChain, m => m.Equals(cur, StringComparison.OrdinalIgnoreCase));
        if (defaultIdx >= 0 && defaultIdx < defaultPriorityChain.Length - 1)
        {
            return defaultPriorityChain[defaultIdx + 1];
        }

        if (!cur.Equals("gemini-3.6-flash", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini-3.6-flash";
        }
        if (!cur.Equals("gemini-3.1-flash-lite", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini-3.1-flash-lite";
        }

        return "";
    }
}
