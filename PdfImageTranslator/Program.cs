using System;
using System.IO;
using System.Linq;
using System.Text;
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
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("PDF to Text Converter with OCR Support");
        Console.WriteLine("======================================\n");

        // Parse command line arguments
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: PdfImageTranslator <pdf-file-path> [output-text-file-path]");
            Console.WriteLine("\nExample:");
            Console.WriteLine("  PdfImageTranslator input.pdf");
            Console.WriteLine("  PdfImageTranslator input.pdf output.txt");
            return 1;
        }

        string pdfPath = args[0];
        string outputPath = args.Length > 1 ? args[1] : Path.ChangeExtension(pdfPath, ".txt");

        // Validate input file
        if (!File.Exists(pdfPath))
        {
            Console.WriteLine($"Error: File not found: {pdfPath}");
            return 1;
        }

        try
        {
            // Load configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            string? apiKey = configuration["OpenAI:ApiKey"] 
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_OPENAI_API_KEY_HERE")
            {
                Console.WriteLine("Warning: OpenAI API key not configured.");
                Console.WriteLine("Set it in appsettings.json or OPENAI_API_KEY environment variable.");
                Console.WriteLine("OCR functionality will not be available.\n");
                apiKey = null;
            }

            // Process PDF
            string extractedText = await ProcessPdfAsync(pdfPath, apiKey);

            // Write output
            await File.WriteAllTextAsync(outputPath, extractedText);

            Console.WriteLine($"\n✓ Text extracted successfully!");
            Console.WriteLine($"Output saved to: {outputPath}");
            Console.WriteLine($"Total characters: {extractedText.Length}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return 1;
        }
    }

    static async Task<string> ProcessPdfAsync(string pdfPath, string? apiKey)
    {
        Console.WriteLine($"Processing PDF: {pdfPath}\n");

        var result = new StringBuilder();
        int pageCount = 0;
        int textPagesCount = 0;
        int imagePagesCount = 0;

        using (var document = PdfDocument.Open(pdfPath))
        {
            pageCount = document.NumberOfPages;
            Console.WriteLine($"Total pages: {pageCount}\n");

            for (int i = 1; i <= pageCount; i++)
            {
                Console.Write($"Processing page {i}/{pageCount}... ");

                Page page = document.GetPage(i);
                string pageText = page.Text.Trim();

                // Check if page has extractable text
                if (!string.IsNullOrWhiteSpace(pageText) && pageText.Length > 50)
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
                            string ocrText = await ExtractTextFromImagePageAsync(pdfPath, i, apiKey);
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

    static async Task<string> ExtractTextFromImagePageAsync(string pdfPath, int pageNumber, string apiKey)
    {
        // Render PDF page to image
        using var pageImage = PDFtoImage.Conversion.ToImage(pdfPath, page: pageNumber - 1); // 0-based index
        
        // Convert SkiaSharp bitmap to base64
        using var image = SKImage.FromBitmap(pageImage);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        byte[] imageBytes = data.ToArray();
        string base64Image = Convert.ToBase64String(imageBytes);

        // Call OpenAI Vision API
        var client = new OpenAIClient(apiKey);
        var chatClient = client.GetChatClient("gpt-4o");

        var messages = new ChatMessage[]
        {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart("請將此圖片中的所有文字完整且準確地轉錄成文字。請逐字轉錄，保持原有的格式和段落結構。"),
                ChatMessageContentPart.CreateImagePart(new Uri($"data:image/jpeg;base64,{base64Image}"))
            )
        };

        var completionOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 4096,
            Temperature = 0.0f
        };

        var response = await chatClient.CompleteChatAsync(messages, completionOptions);

        return response.Value.Content[0].Text;
    }
}
