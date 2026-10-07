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
                using var pdf = PdfDocument.Open(path);
                foreach (var page in pdf.GetPages())
                {
                    var images = page.GetImages();
                    foreach (var img in images)
                    {
                        if (img.TryGetPng(out var bytes))
                        {
                            result.Add(new ImagePayload("image/png", Convert.ToBase64String(bytes)));
                        }
                        else
                        {
                            var rawBytes = img.RawBytes.ToArray();
                            result.Add(new ImagePayload("image/png", Convert.ToBase64String(rawBytes)));
                        }
                    }
                }
            }
            else if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tif" or ".tiff")
            {
                var bytes = File.ReadAllBytes(path);
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

    public static async Task<string> ConvertToWordHtmlAsync(
        string apiKey,
        List<ImagePayload> images,
        string modelName = "gemini-3.1-flash-lite",
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
        {
            throw new InvalidOperationException("Không có hình ảnh nào được chọn để xử lý.");
        }

        onProgress?.Invoke($"Đang chuẩn bị gửi {images.Count} trang ảnh đến Gemini ({modelName})...");

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";

        var partsList = new List<object>
        {
            new { text = PromptConstants.SystemPrompt },
            new { text = "\nDưới đây là toàn bộ hình ảnh tài liệu cần số hóa chuẩn Word HTML:" }
        };

        for (int i = 0; i < images.Count; i++)
        {
            partsList.Add(new { text = $"\n--- ẢNH TRANG {i + 1} ---" });
            partsList.Add(new
            {
                inline_data = new
                {
                    mime_type = images[i].MimeType,
                    data = images[i].Base64Data
                }
            });
        }

        var requestBody = new
        {
            contents = new[]
            {
                new { parts = partsList }
            },
            generationConfig = new
            {
                temperature = 0.1
            }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        onProgress?.Invoke("Đang gửi yêu cầu bóc tách OCR và tạo định dạng Word...");

        const int maxRetries = 3;
        int delaySeconds = 2;
        HttpResponseMessage? response = null;
        string responseString = "";

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };

            response = await HttpClient.SendAsync(reqMsg, cancellationToken);
            responseString = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                break;
            }

            // Mã lỗi 503 (ServiceUnavailable) hoặc 429 (TooManyRequests / Quota spike)
            if ((int)response.StatusCode == 503 || (int)response.StatusCode == 429)
            {
                if (attempt < maxRetries)
                {
                    onProgress?.Invoke($"Máy chủ Gemini đang quá tải ({modelName}), đang tự động thử lại lần {attempt}/{maxRetries} sau {delaySeconds}s...");
                    await Task.Delay(delaySeconds * 1000, cancellationToken);
                    delaySeconds *= 2; // Tăng dần thời gian chờ: 2s -> 4s
                    continue;
                }
            }

            // Nếu đã hết số lần retry hoặc gặp lỗi khác
            var friendlyMsg = ((int)response.StatusCode == 503)
                ? $"Máy chủ Gemini cho mô hình '{modelName}' đang tạm thời quá tải (Error 503 - High Demand).\n\n💡 Mẹo xử lý:\n1. Bạn hãy chọn mô hình khác trong danh sách (ví dụ: 'gemini-2.5-flash' hoặc 'gemini-1.5-flash').\n2. Hoặc đợi 10-20 giây rồi bấm bắt đầu lại."
                : $"Lỗi từ Gemini API ({response.StatusCode}):\n{responseString}";

            throw new Exception(friendlyMsg);
        }

        using var doc = JsonDocument.Parse(responseString);
        var root = doc.RootElement;
        
        string generatedText = "";
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCand = candidates[0];
            if (firstCand.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.GetArrayLength() > 0)
            {
                generatedText = parts[0].GetProperty("text").GetString() ?? "";
            }
        }

        return CleanHtmlOutput(generatedText);
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
                        if (lower.StartsWith("gemini-1.") || lower.StartsWith("gemini-2."))
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

        // 1. Danh sách ưu tiên hàng đầu (đang hoạt động 100%, ưu tiên gemini-3.1-flash-lite làm mặc định):
        string[] priorityList = {
            "gemini-3.1-flash-lite",
            "gemini-3.6-flash",
            "gemini-3.5-flash-lite",
            "gemini-3.7-flash",
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
            curatedModels.AddRange(new[] { "gemini-3.1-flash-lite", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.7-flash" });
        }

        // Giới hạn danh sách tối đa 4-5 mô hình tốt nhất để giao diện gọn gàng, người dùng không chọn nhầm
        return curatedModels.Take(5).ToList();
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

        // Tự động chuyển đổi các thẻ <p> chứa tab-stops ABCD sang <table> ẩn viền nếu AI lỡ xuất dạng tab-stops
        // giúp triệt tiêu 100% lỗi phương án D bị nhảy/rớt dòng trong Microsoft Word
        result = Regex.Replace(result, @"<p[^>]*tab-stops:[^>]*>(.*?)</p>", match =>
        {
            var content = match.Groups[1].Value;
            var parts = Regex.Split(content, @"<span[^>]*mso-tab-count:1[^>]*>&#9;</span>", RegexOptions.IgnoreCase)
                             .Select(p => p.Trim())
                             .Where(p => !string.IsNullOrEmpty(p))
                             .ToList();

            if (parts.Count == 4)
            {
                var tds = string.Join("", parts.Select(p => $"<td style=\"width: 25%; border: none; padding: 1pt 4pt 1pt 0; vertical-align: top;\">{p}</td>"));
                return $"<table style=\"width: 100%; border: none; border-collapse: collapse; margin: 2pt 0 4pt 0;\"><tr>{tds}</tr></table>";
            }
            else if (parts.Count == 2)
            {
                var tds = string.Join("", parts.Select(p => $"<td style=\"width: 50%; border: none; padding: 1pt 6pt 1pt 0; vertical-align: top;\">{p}</td>"));
                return $"<table style=\"width: 100%; border: none; border-collapse: collapse; margin: 2pt 0 4pt 0;\"><tr>{tds}</tr></table>";
            }
            return match.Value;
        }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return result;
    }
}
