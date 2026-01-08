using UglyToad.PdfPig;

namespace PdfToTextOcr;

/// <summary>
/// Extracts text content from PDF files using PdfPig library.
/// Returns text for each page in order.
/// </summary>
public class PdfTextExtractor : IDisposable
{
    private readonly PdfDocument _document;
    private bool _disposed;

    public PdfTextExtractor(string pdfPath)
    {
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException($"PDF file not found: {pdfPath}");
        }
        _document = PdfDocument.Open(pdfPath);
    }

    /// <summary>
    /// Gets the total number of pages in the PDF.
    /// </summary>
    public int PageCount => _document.NumberOfPages;

    /// <summary>
    /// Extracts text from a specific page (1-based index).
    /// </summary>
    /// <param name="pageNumber">Page number (1-based)</param>
    /// <returns>The text content of the page</returns>
    public string GetPageText(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > _document.NumberOfPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), 
                $"Page number must be between 1 and {_document.NumberOfPages}");
        }

        var page = _document.GetPage(pageNumber);
        return page.Text ?? string.Empty;
    }

    /// <summary>
    /// Extracts text from all pages.
    /// </summary>
    /// <returns>An enumerable of page texts in order</returns>
    public IEnumerable<string> GetAllPageTexts()
    {
        for (int i = 1; i <= _document.NumberOfPages; i++)
        {
            yield return GetPageText(i);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _document.Dispose();
            _disposed = true;
        }
    }
}
