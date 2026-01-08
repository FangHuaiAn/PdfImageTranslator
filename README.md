# PdfImageTranslator

C# Console App，將 PDF 轉為純文字；若 PDF 為掃描影像型（無可用文字層），則將每頁渲染成影像後呼叫 OpenAI 視覺模型逐字轉錄成文字，再按頁序合併輸出。

A C# Console Application that converts PDF files to plain text. For scanned image-based PDFs (without text layer), it renders each page as an image and uses OpenAI's Vision model to transcribe the content into text, then merges the output in page order.

## Features

- **Text-based PDF**: Extracts text directly from PDFs with text layers
- **Image-based PDF (OCR)**: Renders pages as images and uses OpenAI's GPT-4 Vision model for OCR
- **Hybrid PDFs**: Automatically detects and processes both text and image pages accordingly
- **Command-line interface**: Simple and easy to use
- **Configurable**: API key can be set via configuration file or environment variable

## Prerequisites

- .NET 9.0 SDK or later
- OpenAI API key (only required for OCR functionality)

## Installation

1. Clone this repository
2. Navigate to the PdfImageTranslator directory
3. Build the project:
   ```bash
   dotnet build
   ```

## Configuration

### Option 1: Configuration File

Edit `appsettings.json` and add your OpenAI API key:

```json
{
  "OpenAI": {
    "ApiKey": "your-api-key-here"
  }
}
```

### Option 2: Environment Variable

Set the `OPENAI_API_KEY` environment variable:

**Windows:**
```cmd
set OPENAI_API_KEY=your-api-key-here
```

**Linux/macOS:**
```bash
export OPENAI_API_KEY=your-api-key-here
```

**Note:** If no API key is configured, the application will still work for text-based PDFs but cannot perform OCR on scanned image PDFs.

## Usage

### Basic Usage

```bash
dotnet run --project PdfImageTranslator <pdf-file-path>
```

This will create a text file with the same name as the input PDF but with `.txt` extension.

### Specify Output File

```bash
dotnet run --project PdfImageTranslator <pdf-file-path> <output-text-file-path>
```

### Examples

```bash
# Process a PDF and save to default output location
dotnet run --project PdfImageTranslator document.pdf

# Process a PDF and save to specific output file
dotnet run --project PdfImageTranslator document.pdf output.txt

# Run the compiled executable directly
./PdfImageTranslator/bin/Debug/net9.0/PdfImageTranslator document.pdf
```

## How It Works

1. **Opens the PDF** and analyzes each page
2. **For pages with text layer**: Extracts text directly using PdfPig
3. **For pages without text layer**: 
   - Renders the page as a high-quality image using PDFtoImage
   - Sends the image to OpenAI's GPT-4 Vision model
   - Receives transcribed text from the AI
4. **Merges all pages** in order and saves to output file

## Output Format

The output text file contains:
- Page markers (`--- Page N ---`) for each page
- Extracted or transcribed text content
- Page separators for easy navigation

## Dependencies

- **UglyToad.PdfPig**: PDF text extraction
- **PDFtoImage**: PDF page rendering to images
- **OpenAI**: OpenAI API client for GPT-4 Vision
- **Microsoft.Extensions.Configuration**: Configuration management
- **SkiaSharp**: Image processing (included with PDFtoImage)

## Limitations

- OCR functionality requires a valid OpenAI API key
- OCR processing may take time depending on page count and complexity
- API usage costs apply for OpenAI Vision API calls
- Large PDFs with many image pages may incur significant API costs

## License

MIT License - See LICENSE file for details
