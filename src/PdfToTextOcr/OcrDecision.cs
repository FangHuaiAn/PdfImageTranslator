namespace PdfToTextOcr;

/// <summary>
/// Determines whether OCR is needed based on text layer extraction results.
/// 
/// Text Layer Threshold:
/// - We count non-whitespace characters across all pages
/// - If the total effective character count >= 200, we consider the PDF
///   to have a valid text layer and use the extracted text directly
/// - If < 200 effective characters, we fall back to OCR
/// 
/// The threshold of 200 characters is chosen to:
/// - Filter out PDFs with only metadata or minimal content
/// - Allow for PDFs that might have some text but are primarily scanned images
/// - A typical paragraph of text contains 300-500 characters
/// </summary>
public static class OcrDecision
{
    /// <summary>
    /// Minimum number of non-whitespace characters required to consider
    /// the text layer as valid and usable.
    /// </summary>
    public const int EffectiveCharThreshold = 200;

    /// <summary>
    /// Counts effective (non-whitespace) characters in a text.
    /// </summary>
    /// <param name="text">The text to analyze</param>
    /// <returns>Count of non-whitespace characters</returns>
    public static int CountEffectiveChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return text.Count(c => !char.IsWhiteSpace(c));
    }

    /// <summary>
    /// Counts total effective characters across all page texts.
    /// </summary>
    /// <param name="pageTexts">Collection of text from each page</param>
    /// <returns>Total count of non-whitespace characters</returns>
    public static int CountTotalEffectiveChars(IEnumerable<string> pageTexts)
    {
        return pageTexts.Sum(CountEffectiveChars);
    }

    /// <summary>
    /// Determines if OCR is required based on extracted text.
    /// </summary>
    /// <param name="pageTexts">Collection of text from each page</param>
    /// <returns>True if OCR is needed, false if text layer is sufficient</returns>
    public static bool RequiresOcr(IEnumerable<string> pageTexts)
    {
        var totalChars = CountTotalEffectiveChars(pageTexts);
        return totalChars < EffectiveCharThreshold;
    }

    /// <summary>
    /// Determines if OCR is required based on extracted text and provides the character count.
    /// </summary>
    /// <param name="pageTexts">Collection of text from each page</param>
    /// <param name="effectiveCharCount">Output: the total effective character count</param>
    /// <returns>True if OCR is needed, false if text layer is sufficient</returns>
    public static bool RequiresOcr(IEnumerable<string> pageTexts, out int effectiveCharCount)
    {
        var textList = pageTexts.ToList();
        effectiveCharCount = CountTotalEffectiveChars(textList);
        return effectiveCharCount < EffectiveCharThreshold;
    }
}
