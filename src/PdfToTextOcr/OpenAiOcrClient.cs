using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfToTextOcr;

/// <summary>
/// Client for calling OpenAI Vision API to perform OCR on images.
/// Uses the REST API directly without third-party SDKs.
/// 
/// API Endpoint: POST https://api.openai.com/v1/responses
/// Model: gpt-4.1-mini
/// 
/// Retry Logic:
/// - Implements exponential backoff with jitter for transient errors
/// - Retries on HTTP 429 (rate limit), 500, 502, 503, 504 (server errors)
/// - Maximum 6 retry attempts
/// </summary>
public class OpenAiOcrClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly Random _random;
    private bool _disposed;

    private const string ApiEndpoint = "https://api.openai.com/v1/responses";
    private const string Model = "gpt-4.1-mini";
    private const int MaxRetries = 6;
    private const int BaseDelayMs = 1000; // Base delay for exponential backoff

    // HTTP status codes that trigger retry
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        HttpStatusCode.TooManyRequests,        // 429
        HttpStatusCode.InternalServerError,    // 500
        HttpStatusCode.BadGateway,             // 502
        HttpStatusCode.ServiceUnavailable,     // 503
        HttpStatusCode.GatewayTimeout          // 504
    };

    public OpenAiOcrClient(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
        }

        _apiKey = apiKey;
        _random = new Random();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _httpClient.Timeout = TimeSpan.FromMinutes(5); // Allow time for large image processing
    }

    /// <summary>
    /// Performs OCR on a PNG image using OpenAI Vision API.
    /// </summary>
    /// <param name="pngBytes">PNG image as byte array</param>
    /// <param name="pageNumber">Page number for logging (1-based)</param>
    /// <returns>Extracted text from the image</returns>
    public async Task<string> PerformOcrAsync(byte[] pngBytes, int pageNumber)
    {
        var base64Image = Convert.ToBase64String(pngBytes);
        var dataUrl = $"data:image/png;base64,{base64Image}";
        return await PerformOcrWithDataUrlAsync(dataUrl, pageNumber);
    }

    /// <summary>
    /// Performs OCR on an image using its data URL.
    /// </summary>
    /// <param name="dataUrl">Base64 data URL of the image</param>
    /// <param name="pageNumber">Page number for logging (1-based)</param>
    /// <returns>Extracted text from the image</returns>
    public async Task<string> PerformOcrWithDataUrlAsync(string dataUrl, int pageNumber)
    {
        // Build the request payload according to OpenAI API specification
        // Request format uses the "input" array with user message containing text and image
        var request = new OpenAiRequest
        {
            Model = Model,
            Input = new List<InputMessage>
            {
                new InputMessage
                {
                    Role = "user",
                    Content = new List<ContentItem>
                    {
                        new TextContentItem
                        {
                            Type = "input_text",
                            Text = "逐字轉錄圖片內所有可見文字；保留換行與標點；不要解釋、不要補字、不要推測；看不清楚就輸出[illegible]。"
                        },
                        new ImageContentItem
                        {
                            Type = "input_image",
                            ImageUrl = dataUrl
                        }
                    }
                }
            }
        };

        var jsonPayload = JsonSerializer.Serialize(request, JsonContext.Default.OpenAiRequest);
        
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(ApiEndpoint, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    return ParseResponse(responseBody);
                }

                var statusCode = response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync();
                var truncatedBody = errorBody.Length > 500 ? errorBody[..500] + "..." : errorBody;

                if (RetryableStatusCodes.Contains(statusCode) && attempt < MaxRetries)
                {
                    var delay = CalculateBackoffDelay(attempt);
                    Console.WriteLine($"  [Page {pageNumber}] HTTP {(int)statusCode} - Retry {attempt}/{MaxRetries} in {delay}ms");
                    await Task.Delay(delay);
                    continue;
                }

                throw new OpenAiApiException(
                    $"OpenAI API error for page {pageNumber}: HTTP {(int)statusCode} {statusCode}. Body: {truncatedBody}",
                    statusCode,
                    truncatedBody);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                var delay = CalculateBackoffDelay(attempt);
                Console.WriteLine($"  [Page {pageNumber}] Network error - Retry {attempt}/{MaxRetries} in {delay}ms: {ex.Message}");
                await Task.Delay(delay);
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken == default && attempt < MaxRetries)
            {
                // Timeout
                var delay = CalculateBackoffDelay(attempt);
                Console.WriteLine($"  [Page {pageNumber}] Timeout - Retry {attempt}/{MaxRetries} in {delay}ms");
                await Task.Delay(delay);
            }
        }
    }

    /// <summary>
    /// Parses the OpenAI API response to extract the text content.
    /// Handles responses where content may be in multiple segments.
    /// 
    /// Response Parsing:
    /// - The response contains an "output" array with assistant messages
    /// - Each message has a "content" array with text segments
    /// - We concatenate all text segments to form the complete response
    /// </summary>
    private static string ParseResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var textBuilder = new StringBuilder();

        // Navigate to output array
        if (root.TryGetProperty("output", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var outputItem in outputArray.EnumerateArray())
            {
                // Look for message type items
                if (outputItem.TryGetProperty("type", out var typeProp) && 
                    typeProp.GetString() == "message")
                {
                    if (outputItem.TryGetProperty("content", out var contentArray) && 
                        contentArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var contentItem in contentArray.EnumerateArray())
                        {
                            // Extract text from output_text type items
                            if (contentItem.TryGetProperty("type", out var contentType) &&
                                contentType.GetString() == "output_text")
                            {
                                if (contentItem.TryGetProperty("text", out var textProp))
                                {
                                    if (textBuilder.Length > 0)
                                        textBuilder.AppendLine();
                                    textBuilder.Append(textProp.GetString());
                                }
                            }
                        }
                    }
                }
            }
        }

        // Fallback: try to find text in a simpler structure
        if (textBuilder.Length == 0 && root.TryGetProperty("choices", out var choices) && 
            choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var content))
                    {
                        if (textBuilder.Length > 0)
                            textBuilder.AppendLine();
                        textBuilder.Append(content.GetString());
                    }
                }
            }
        }

        return textBuilder.ToString();
    }

    /// <summary>
    /// Calculates delay with exponential backoff and jitter.
    /// </summary>
    private int CalculateBackoffDelay(int attempt)
    {
        // Exponential backoff: baseDelay * 2^(attempt-1)
        var exponentialDelay = BaseDelayMs * (1 << (attempt - 1));
        // Cap at 60 seconds
        exponentialDelay = Math.Min(exponentialDelay, 60000);
        // Add jitter: ±25%
        var jitter = (int)(exponentialDelay * 0.25 * (_random.NextDouble() * 2 - 1));
        return exponentialDelay + jitter;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Exception thrown when OpenAI API returns an error.
/// </summary>
public class OpenAiApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public OpenAiApiException(string message, HttpStatusCode statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

#region Request/Response Models

public class OpenAiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public List<InputMessage> Input { get; set; } = new();
}

public class InputMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<ContentItem> Content { get; set; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContentItem), "input_text")]
[JsonDerivedType(typeof(ImageContentItem), "input_image")]
public abstract class ContentItem
{
    [JsonPropertyName("type")]
    public abstract string Type { get; set; }
}

public class TextContentItem : ContentItem
{
    [JsonPropertyName("type")]
    public override string Type { get; set; } = "input_text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class ImageContentItem : ContentItem
{
    [JsonPropertyName("type")]
    public override string Type { get; set; } = "input_image";

    [JsonPropertyName("image_url")]
    public string ImageUrl { get; set; } = "";
}

#endregion

/// <summary>
/// JSON serialization context for AOT compatibility and performance.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenAiRequest))]
[JsonSerializable(typeof(InputMessage))]
[JsonSerializable(typeof(ContentItem))]
[JsonSerializable(typeof(TextContentItem))]
[JsonSerializable(typeof(ImageContentItem))]
internal partial class JsonContext : JsonSerializerContext
{
}
