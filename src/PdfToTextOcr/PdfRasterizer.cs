using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace PdfToTextOcr;

/// <summary>
/// Renders PDF pages to PNG images using Docnet.Core library.
/// Targets 300 DPI equivalent resolution for OCR quality.
/// 
/// DPI/Pixel Control:
/// - Standard PDF page is 612×792 points (8.5×11 inches at 72 DPI)
/// - For 300 DPI equivalent, we scale by factor of ~4.17 (300/72)
/// - Docnet uses a scale factor approach, where 1.0 = 72 DPI
/// - We use scale factor 4.0 which gives approximately 288 DPI (close to 300)
/// </summary>
public class PdfRasterizer : IDisposable
{
    private readonly IDocLib _docLib;
    private readonly IDocReader _reader;
    private bool _disposed;

    // Scale factor for rendering: 4.0 gives ~288 DPI (close to 300 DPI target)
    // This is calculated as: targetDPI / baseDPI = 300 / 72 ≈ 4.17
    // We use 4.0 for a round number that's close enough
    private const int ScaleFactor = 4;

    public PdfRasterizer(string pdfPath)
    {
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException($"PDF file not found: {pdfPath}");
        }

        _docLib = DocLib.Instance;
        _reader = _docLib.GetDocReader(pdfPath, new PageDimensions(ScaleFactor));
    }

    /// <summary>
    /// Gets the total number of pages in the PDF.
    /// </summary>
    public int PageCount => _reader.GetPageCount();

    /// <summary>
    /// Renders a specific page to PNG bytes (in-memory, no file I/O).
    /// </summary>
    /// <param name="pageNumber">Page number (0-based index)</param>
    /// <returns>PNG image as byte array</returns>
    public byte[] RenderPageToPng(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= _reader.GetPageCount())
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                $"Page number must be between 0 and {_reader.GetPageCount() - 1}");
        }

        using var pageReader = _reader.GetPageReader(pageNumber);
        var width = pageReader.GetPageWidth();
        var height = pageReader.GetPageHeight();
        var rawBytes = pageReader.GetImage();

        // Convert raw BGRA bytes to PNG format
        return ConvertBgraToPng(rawBytes, width, height);
    }

    /// <summary>
    /// Renders a page and returns it as a base64 data URL.
    /// Format: data:image/png;base64,...
    /// </summary>
    /// <param name="pageNumber">Page number (0-based index)</param>
    /// <returns>Base64 data URL string</returns>
    public string RenderPageToDataUrl(int pageNumber)
    {
        var pngBytes = RenderPageToPng(pageNumber);
        var base64 = Convert.ToBase64String(pngBytes);
        return $"data:image/png;base64,{base64}";
    }

    /// <summary>
    /// Converts raw BGRA pixel data to PNG format.
    /// Uses a simple PNG encoder without external dependencies.
    /// </summary>
    private static byte[] ConvertBgraToPng(byte[] bgraData, int width, int height)
    {
        // Convert BGRA to RGBA
        var rgbaData = new byte[bgraData.Length];
        for (int i = 0; i < bgraData.Length; i += 4)
        {
            rgbaData[i] = bgraData[i + 2];     // R (was B)
            rgbaData[i + 1] = bgraData[i + 1]; // G (stays G)
            rgbaData[i + 2] = bgraData[i];     // B (was R)
            rgbaData[i + 3] = bgraData[i + 3]; // A (stays A)
        }

        return EncodePng(rgbaData, width, height);
    }

    /// <summary>
    /// Simple PNG encoder for RGBA data.
    /// </summary>
    private static byte[] EncodePng(byte[] rgbaData, int width, int height)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // PNG Signature
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR chunk
        var ihdr = new byte[13];
        WriteInt32BigEndian(ihdr, 0, width);
        WriteInt32BigEndian(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type (RGBA)
        ihdr[10] = 0; // compression method
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // interlace method
        WriteChunk(bw, "IHDR", ihdr);

        // IDAT chunk (image data)
        var rawData = new byte[height * (1 + width * 4)]; // filter byte + RGBA per row
        var srcIndex = 0;
        var dstIndex = 0;
        for (int y = 0; y < height; y++)
        {
            rawData[dstIndex++] = 0; // filter type: None
            for (int x = 0; x < width * 4; x++)
            {
                rawData[dstIndex++] = rgbaData[srcIndex++];
            }
        }

        // Compress with zlib
        var compressedData = CompressZlib(rawData);
        WriteChunk(bw, "IDAT", compressedData);

        // IEND chunk
        WriteChunk(bw, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteInt32BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var lengthBytes = new byte[4];
        WriteInt32BigEndian(lengthBytes, 0, data.Length);
        bw.Write(lengthBytes);
        bw.Write(typeBytes);
        bw.Write(data);

        // CRC32 of type + data
        var crcData = new byte[4 + data.Length];
        Array.Copy(typeBytes, 0, crcData, 0, 4);
        Array.Copy(data, 0, crcData, 4, data.Length);
        var crc = CalculateCrc32(crcData);
        var crcBytes = new byte[4];
        WriteInt32BigEndian(crcBytes, 0, (int)crc);
        bw.Write(crcBytes);
    }

    private static uint CalculateCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 * (crc & 1));
            }
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var output = new MemoryStream();
        
        // Zlib header (default compression, no dict)
        output.WriteByte(0x78);
        output.WriteByte(0x9C);

        // Deflate compress
        using (var deflate = new System.IO.Compression.DeflateStream(output, 
            System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        // Adler-32 checksum
        var adler = CalculateAdler32(data);
        var adlerBytes = new byte[4];
        WriteInt32BigEndian(adlerBytes, 0, (int)adler);
        output.Write(adlerBytes, 0, 4);

        return output.ToArray();
    }

    private static uint CalculateAdler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var c in data)
        {
            a = (a + c) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reader.Dispose();
            _disposed = true;
        }
    }
}
