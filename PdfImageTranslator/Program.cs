﻿﻿﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PdfImageTranslator;

class Program
{
    // Configuration constants
    private const int MinTextLengthThreshold = 10;
    private const int JpegQuality = 85;
    private const int MaxTokens = 4096;
    private const float ImageScaleFactor = 2.0f; // 圖片放大倍數
    private const string OcrPrompt = @"Please transcribe all text from this image and provide a bilingual (English-Chinese) output.

Instructions:
1. The document is written in English. First, accurately recognize and correct any OCR errors caused by image distortion, such as:
   - 'rn' misread as 'm', or 'm' misread as 'rn'
   - '0' (zero) confused with 'O' (letter O)
   - '1' (one) confused with 'l' (lowercase L) or 'I' (uppercase i)
   - 'cl' misread as 'd', or 'd' misread as 'cl'
   - Missing or extra spaces between words
   - Broken words due to line wrapping
2. After recognition, translate the text paragraph by paragraph into Traditional Chinese (繁體中文).
3. Preserve the original paragraph structure.
4. Output in Markdown format with the following bilingual structure:
   - First, output all the recognized English text under the heading '## English'
   - Then, output the translated Traditional Chinese text under the heading '## 繁體中文'
5. Both sections should maintain the same paragraph structure for easy comparison.";

    private const string TranslatePrompt = @"Please translate the following English text into Traditional Chinese and provide a bilingual (English-Chinese) output.

Instructions:
1. Translate the text paragraph by paragraph into Traditional Chinese (繁體中文).
2. Preserve the original paragraph structure.
3. Output in Markdown format with the following bilingual structure:
   - First, output the original English text under the heading '## English'
   - Then, output the translated Traditional Chinese text under the heading '## 繁體中文'
4. Both sections should maintain the same paragraph structure for easy comparison.

Here is the text to translate:
";
    
    // Gemini Configuration
    private static readonly HttpClient httpClient = new HttpClient();

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("PDF to Text Converter with OCR Support");
        Console.WriteLine("======================================\n");

        try
        {
            // Load configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            string inputPath = configuration["Settings:InputPath"] ?? "./p1dfs";
            string outputPath = configuration["Settings:OutputPath"] ?? "./o2utput";
            string aiProvider = configuration["Settings:AiProvider"] ?? "OpenAI";
            string? pageRange = configuration["Settings:Pages"];

            string? modelId = null;
            string? apiKey = null;
            if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configuration["Gemini:ApiKey"];
                modelId = configuration["Gemini:ModelId"] ?? "gemini-1.5-pro";
            }
            else
            {
                // Default to OpenAI
                aiProvider = "OpenAI";
                apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? configuration["OpenAI:ApiKey"];
            }

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_OPENAI_API_KEY_HERE" || apiKey == "YOUR_GEMINI_API_KEY_HERE")
            {
                Console.WriteLine($"Warning: API key for {aiProvider} not configured.");
                Console.WriteLine($"Set it in appsettings.json or environment variable.");
                Console.WriteLine("OCR functionality will not be available.\n");
                apiKey = null;
            }
            
            Console.WriteLine($"Using AI Provider: {aiProvider}");

            if (!Directory.Exists(inputPath))
            {
                Console.WriteLine($"Error: Input directory not found: {inputPath}");
                return 1;
            }

            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            var pdfFiles = Directory.GetFiles(inputPath, "*.pdf", SearchOption.AllDirectories);
            Console.WriteLine($"Found {pdfFiles.Length} PDF files in '{inputPath}'\n");

            foreach (var pdfPath in pdfFiles)
            {
                try
                {
                    string extractedText = await ProcessPdfAsync(pdfPath, apiKey, aiProvider, modelId, pageRange);
                    string outputFilePath = Path.Combine(outputPath, Path.ChangeExtension(Path.GetFileName(pdfPath), ".md"));
                    await File.WriteAllTextAsync(outputFilePath, extractedText);
                    Console.WriteLine($"Saved to: {outputFilePath}");
                    Console.WriteLine("--------------------------------------------------\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing {Path.GetFileName(pdfPath)}: {ex.Message}\n");
                }
            }
            
            Console.WriteLine("Batch processing completed.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return 1;
        }
    }

    static async Task<string> ProcessPdfAsync(string pdfPath, string? apiKey, string aiProvider, string? modelId, string? pageRange = null)
    {
        Console.WriteLine($"Processing PDF: {pdfPath}\n");

        var result = new StringBuilder();
        int pageCount = 0;
        int textPagesCount = 0;
        int imagePagesCount = 0;

        Func<int, bool> shouldProcessPage = _ => true;
        if (!string.IsNullOrWhiteSpace(pageRange))
        {
            var pagesToProcess = new HashSet<int>();
            var parts = pageRange.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Contains('-'))
                {
                    var rangeParts = trimmed.Split('-');
                    if (rangeParts.Length == 2 && int.TryParse(rangeParts[0], out int start) && int.TryParse(rangeParts[1], out int end))
                    {
                        for (int p = start; p <= end; p++) pagesToProcess.Add(p);
                    }
                }
                else
                {
                    if (int.TryParse(trimmed, out int pageNum))
                    {
                        pagesToProcess.Add(pageNum);
                    }
                }
            }
            shouldProcessPage = p => pagesToProcess.Contains(p);
            Console.WriteLine($"Processing specific pages: {pageRange}");
        }

        using (var document = PdfDocument.Open(pdfPath))
        {
            pageCount = document.NumberOfPages;
            Console.WriteLine($"Total pages: {pageCount}\n");

            for (int i = 1; i <= pageCount; i++)
            {
                if (!shouldProcessPage(i)) continue;

                Console.Write($"Processing page {i}/{pageCount}... ");

                Page page = document.GetPage(i);
                string pageText = page.Text.Trim();

                // Check if page has extractable text
                if (!string.IsNullOrWhiteSpace(pageText) && pageText.Length > MinTextLengthThreshold)
                {
                    // Page has text layer, translate the text
                    if (apiKey == null)
                    {
                        result.AppendLine($"--- Page {i} ---");
                        result.AppendLine(pageText);
                        result.AppendLine();
                        Console.WriteLine("✓ Text extracted (no translation - API key not available)");
                    }
                    else
                    {
                        try
                        {
                            string translatedText = await TranslateTextAsync(pageText, apiKey, aiProvider, modelId);
                            result.AppendLine($"--- Page {i} ---");
                            result.AppendLine(translatedText);
                            result.AppendLine();
                            textPagesCount++;
                            Console.WriteLine("✓ Text translated");
                        }
                        catch (Exception ex)
                        {
                            result.AppendLine($"--- Page {i} ---");
                            result.AppendLine(pageText);
                            result.AppendLine($"[Translation Error: {ex.Message}]");
                            result.AppendLine();
                            Console.WriteLine($"✗ Translation failed: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // Page is likely a scanned image, use OCR
                    if (apiKey == null)
                    {
                        result.AppendLine($"--- Page {i} ---");
                        result.AppendLine("[No text content found and OCR not available]");
                        result.AppendLine();
                        Console.WriteLine("⚠ No text (OCR not available)");
                    }
                    else
                    {
                        try
                        {
                            string ocrText = await ExtractTextFromImagePageAsync(pdfPath, page, apiKey, aiProvider, modelId);
                            result.AppendLine($"--- Page {i} ---");
                            result.AppendLine(ocrText);
                            result.AppendLine();
                            imagePagesCount++;
                            Console.WriteLine("✓ OCR completed");
                        }
                        catch (Exception ex)
                        {
                            result.AppendLine($"--- Page {i} ---");
                            result.AppendLine($"[OCR Error: {ex.Message}]");
                            result.AppendLine();
                            Console.WriteLine($"✗ OCR failed: {ex.Message}");
                        }
                    }
                }
            }
        }

        Console.WriteLine($"\nSummary:");
        Console.WriteLine($"  Text-based pages: {textPagesCount}");
        Console.WriteLine($"  OCR-processed pages: {imagePagesCount}");
        Console.WriteLine($"  Total pages: {pageCount}");

        return result.ToString();
    }

    static async Task<string> ExtractTextFromImagePageAsync(string pdfPath, Page page, string apiKey, string aiProvider, string? modelId)
    {
        byte[]? imageBytes = null;
        string imageMediaType = "image/jpeg";

        // 1. 嘗試直接從 PDF 結構中提取嵌入的圖片 (Optimization)
        // 如果頁面只包含一張圖片(常見於掃描檔)，直接提取可避免重新渲染與壓縮的失真
        try
        {
            var images = page.GetImages().ToList();
            if (images.Count == 1 && images[0].TryGetPng(out byte[] pngBytes))
            {
                // 將提取的 PNG 圖片放大兩倍以提高 OCR 辨識率
                using var originalBitmap = SKBitmap.Decode(pngBytes);
                using var scaledBitmap = ScaleImage(originalBitmap, ImageScaleFactor);
                using var scaledImage = SKImage.FromBitmap(scaledBitmap);
                using var scaledData = scaledImage.Encode(SKEncodedImageFormat.Png, 100);
                imageBytes = scaledData.ToArray();
                imageMediaType = "image/png";
                // Console.WriteLine($"[Debug] Extracted and scaled embedded image (Page {page.Number}, {originalBitmap.Width}x{originalBitmap.Height} -> {scaledBitmap.Width}x{scaledBitmap.Height})");
            }
        }
        catch
        {
            // 若提取失敗，忽略錯誤並進入下方的渲染流程
        }

        // 2. 若無法直接提取，則使用 PDFtoImage 渲染整頁 (Fallback)
        if (imageBytes == null)
        {
            try
            {
                // Render PDF page to image (PDFtoImage uses 0-based index)
                using var pageImage = PDFtoImage.Conversion.ToImage(pdfPath, page: page.Number - 1);
                
                // Convert SkiaSharp bitmap to bytes
                using var image = SKImage.FromBitmap(pageImage);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
                imageBytes = data.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Image Conversion Error] Page {page.Number}: {ex}");
                return $"轉換 Image 失敗: {ex.Message}";
            }
        }

        if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            // Call Gemini API (REST)
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelId}:generateContent?key={apiKey}";
            
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = OcrPrompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = imageMediaType,
                                    data = Convert.ToBase64String(imageBytes!)
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = MaxTokens,
                    temperature = 0.0
                },
                safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
                }
            };

            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            var text = jsonResponse?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

            return text ?? "[Gemini returned no text]";
        }
        else
        {
            // Call OpenAI Vision API
            var client = new OpenAIClient(apiKey);
            var chatClient = client.GetChatClient("gpt-4o");

            var messages = new ChatMessage[]
            {
                new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart(OcrPrompt),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), imageMediaType)
                )
            };

            var completionOptions = new ChatCompletionOptions
            {
                MaxOutputTokenCount = MaxTokens,
                Temperature = 0.0f
            };

            var response = await chatClient.CompleteChatAsync(messages, completionOptions);

            return response.Value.Content[0].Text;
        }
    }

    static async Task<string> TranslateTextAsync(string text, string apiKey, string aiProvider, string? modelId)
    {
        string prompt = TranslatePrompt + text;

        if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            // Call Gemini API (REST)
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelId}:generateContent?key={apiKey}";
            
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = MaxTokens,
                    temperature = 0.0
                },
                safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
                }
            };

            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            var resultText = jsonResponse?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

            return resultText ?? "[Gemini returned no text]";
        }
        else
        {
            // Call OpenAI API
            var client = new OpenAIClient(apiKey);
            var chatClient = client.GetChatClient("gpt-4o");

            var messages = new ChatMessage[]
            {
                new UserChatMessage(ChatMessageContentPart.CreateTextPart(prompt))
            };

            var completionOptions = new ChatCompletionOptions
            {
                MaxOutputTokenCount = MaxTokens,
                Temperature = 0.0f
            };

            var response = await chatClient.CompleteChatAsync(messages, completionOptions);

            return response.Value.Content[0].Text;
        }
    }

    /// <summary>
    /// 將圖片依指定倍數放大，以提高 OCR 辨識率
    /// </summary>
    /// <param name="original">原始圖片</param>
    /// <param name="scaleFactor">放大倍數</param>
    /// <returns>放大後的圖片</returns>
    static SKBitmap ScaleImage(SKBitmap original, float scaleFactor)
    {
        int newWidth = (int)(original.Width * scaleFactor);
        int newHeight = (int)(original.Height * scaleFactor);
        
        var scaledBitmap = new SKBitmap(newWidth, newHeight);
        using var canvas = new SKCanvas(scaledBitmap);
        
        // 使用高品質的縮放演算法 (Cubic for upscaling)
        var samplingOptions = new SKSamplingOptions(SKCubicResampler.Mitchell);
        
        canvas.DrawImage(SKImage.FromBitmap(original), new SKRect(0, 0, newWidth, newHeight), samplingOptions);
        
        return scaledBitmap;
    }
}
