using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OcrWordApp;

/// <summary>
/// Dịch vụ OCR dự phòng dùng DeepSeek (API tương thích OpenAI).
/// Được gọi tự động khi Gemini thất bại (bộ lọc bản quyền RECITATION hoặc lỗi liên tục).
/// </summary>
public class DeepSeekOcrService
{
    private static readonly HttpClient HttpClient = new HttpClient();

    public static async Task<string> ConvertPageAsync(
        string apiKey,
        GeminiOcrService.ImagePayload image,
        string systemPrompt,
        string modelName = "deepseek-flash",
        int pageNum = 1,
        int totalPages = 1,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var dataUrl = BuildImageDataUrl(image);

        var userContent = new List<object>
        {
            new { type = "text", text = $"\n[ĐÂY LÀ TRANG {pageNum}/{totalPages} CỦA BỘ ĐỀ THI. BẮT BUỘC BÓC TÁCH ĐẦY ĐỦ 100% TẤT CẢ CÁC CÂU CỦA TRANG NÀY, KHÔNG ĐƯỢC BỎ SÓT BẤT KỲ CÂU NÀO!]" },
            new { type = "image_url", image_url = new { url = dataUrl } }
        };

        var requestBody = new
        {
            model = modelName,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            },
            temperature = 0.2,
            max_tokens = 65536,
            stream = false
        };

        onProgress?.Invoke($"Trang {pageNum}: Gemini thất bại, đang thử lại bằng DeepSeek ({modelName})...");

        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        reqMsg.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await HttpClient.SendAsync(reqMsg, cancellationToken);
        var responseString = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Lỗi từ DeepSeek API ở Trang {pageNum} ({(int)response.StatusCode}):\n{responseString}");
        }

        using var doc = JsonDocument.Parse(responseString);
        string text = "";
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var contentProp))
            {
                text = contentProp.GetString() ?? "";
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Exception($"DeepSeek trả về nội dung rỗng ở Trang {pageNum}.");
        }

        return GeminiOcrService.CleanHtmlOutput(text);
    }

    /// <summary>
    /// Đóng gói ảnh thành data URL. DeepSeek chỉ nhận JPEG/PNG/GIF/WebP nên các định dạng
    /// khác (BMP, TIFF...) được chuyển sang PNG; PDF gửi trực tiếp không hỗ trợ.
    /// </summary>
    private static string BuildImageDataUrl(GeminiOcrService.ImagePayload image)
    {
        var mime = (image.MimeType ?? "").ToLowerInvariant();

        if (mime is "image/jpeg" or "image/png" or "image/webp" or "image/gif")
        {
            return $"data:{mime};base64,{image.Base64Data}";
        }

        if (mime == "application/pdf")
        {
            throw new Exception("DeepSeek không nhận trực tiếp file PDF. Hãy xử lý PDF bằng Gemini, hoặc chuyển PDF thành ảnh trước.");
        }

        var bytes = Convert.FromBase64String(image.Base64Data);
        using var input = new MemoryStream(bytes);
        using var gdi = System.Drawing.Image.FromStream(input);
        using var output = new MemoryStream();
        gdi.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return $"data:image/png;base64,{Convert.ToBase64String(output.ToArray())}";
    }
}
