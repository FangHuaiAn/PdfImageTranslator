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
    private const int MinTextLengthThreshold = 50;
    private const int JpegQuality = 85;
    private const int MaxTokens = 4096;
    private const string OcrPrompt = "請將此圖片中的所有文字完整且準確地轉錄成文字。請逐字轉錄，保持原有的格式和段落結構。";
    
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

            string inputPath = configuration["Settings:InputPath"] ?? "./pdfs";
            string outputPath = configuration["Settings:OutputPath"] ?? "./output";
            string aiProvider = configuration["Settings:AiProvider"] ?? "OpenAI";
            string? pageRange = configuration["Settings:Pages"];

            string? modelId = null;
            string? apiKey = null;
            if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = configuration["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
                modelId = configuration["Gemini:ModelId"] ?? "gemini-1.5-pro";
            }
            else
            {
                // Default to OpenAI
                aiProvider = "OpenAI";
                apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            }

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_OPENAI_API_KEY_HERE")
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
                    string outputFilePath = Path.Combine(outputPath, Path.ChangeExtension(Path.GetFileName(pdfPath), ".txt"));
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
                    // Page has text layer, extract directly
                    result.AppendLine($"--- Page {i} ---");
                    result.AppendLine(pageText);
                    result.AppendLine();
                    textPagesCount++;
                    Console.WriteLine("✓ Text extracted");
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
                imageBytes = pngBytes;
                imageMediaType = "image/png";
                // Console.WriteLine($"[Debug] Extracted embedded image directly (Page {page.Number})");
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
}
