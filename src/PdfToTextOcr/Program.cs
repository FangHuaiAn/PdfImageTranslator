using System.Text;

namespace PdfToTextOcr;

/// <summary>
/// PDF to Text OCR Tool
/// 
/// Converts PDF files to plain text. If the PDF contains a valid text layer,
/// extracts text directly. For scanned/image-based PDFs, renders each page
/// as an image and uses OpenAI Vision API for OCR.
/// 
/// Usage: PdfToTextOcr.exe <input.pdf> <output.txt>
/// 
/// Environment Variables:
/// - OPENAI_API_KEY: Required for OCR functionality
/// </summary>
class Program
{
    private const string PageSeparator = "=== Page {0} ===";

    static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunAsync(string[] args)
    {
        // Validate command line arguments
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PdfToTextOcr <input.pdf> <output.txt>");
            return 1;
        }

        var inputPath = args[0];
        var outputPath = args[1];

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
            return 1;
        }

        Console.WriteLine($"Processing: {inputPath}");

        // Step 1: Extract text layer
        Console.WriteLine("Extracting text layer...");
        List<string> pageTexts;
        int pageCount;

        using (var extractor = new PdfTextExtractor(inputPath))
        {
            pageCount = extractor.PageCount;
            Console.WriteLine($"  Found {pageCount} page(s)");
            
            pageTexts = extractor.GetAllPageTexts().ToList();
        }

        // Step 2: Decide if OCR is needed
        var requiresOcr = OcrDecision.RequiresOcr(pageTexts, out int effectiveCharCount);
        Console.WriteLine($"  Effective characters: {effectiveCharCount} (threshold: {OcrDecision.EffectiveCharThreshold})");

        // Step 3: Write output
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));

        if (!requiresOcr)
        {
            // Use extracted text directly
            Console.WriteLine("Using text layer (sufficient text content found)");
            await WriteTextLayerOutput(writer, pageTexts);
        }
        else
        {
            // OCR flow
            Console.WriteLine("Text layer insufficient, switching to OCR mode...");
            
            // Check for API key
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine("Error: OPENAI_API_KEY environment variable is not set.");
                Console.Error.WriteLine("Please set the environment variable and try again.");
                return 1;
            }

            await PerformOcrAndWrite(writer, inputPath, pageCount, apiKey);
        }

        Console.WriteLine($"Output written to: {outputPath}");
        return 0;
    }

    /// <summary>
    /// Writes the extracted text layer to output file.
    /// </summary>
    private static async Task WriteTextLayerOutput(StreamWriter writer, List<string> pageTexts)
    {
        for (int i = 0; i < pageTexts.Count; i++)
        {
            var pageNumber = i + 1;
            Console.WriteLine($"  Writing page {pageNumber}/{pageTexts.Count}");

            await writer.WriteLineAsync(string.Format(PageSeparator, pageNumber));
            await writer.WriteLineAsync(pageTexts[i]);
            await writer.WriteLineAsync(); // Blank line between pages
        }
    }

    /// <summary>
    /// Performs OCR on each page and writes to output file.
    /// Uses streaming approach to avoid memory issues with large documents.
    /// </summary>
    private static async Task PerformOcrAndWrite(StreamWriter writer, string inputPath, int pageCount, string apiKey)
    {
        using var rasterizer = new PdfRasterizer(inputPath);
        using var ocrClient = new OpenAiOcrClient(apiKey);

        for (int i = 0; i < pageCount; i++)
        {
            var pageNumber = i + 1;
            Console.WriteLine($"  Processing page {pageNumber}/{pageCount}...");

            // Render page to PNG bytes
            Console.WriteLine($"    Rendering to image...");
            var pngBytes = rasterizer.RenderPageToPng(i);
            Console.WriteLine($"    Image size: {pngBytes.Length / 1024} KB");

            // Perform OCR
            Console.WriteLine($"    Calling OpenAI OCR...");
            var ocrText = await ocrClient.PerformOcrAsync(pngBytes, pageNumber);
            Console.WriteLine($"    Received {ocrText.Length} characters");

            // Write to output
            await writer.WriteLineAsync(string.Format(PageSeparator, pageNumber));
            await writer.WriteLineAsync(ocrText);
            await writer.WriteLineAsync(); // Blank line between pages

            // Flush after each page to ensure data is written
            await writer.FlushAsync();
        }
    }
}
