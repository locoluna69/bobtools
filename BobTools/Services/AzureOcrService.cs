using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using BobTools.Models;
using BobTools.Utils;

namespace BobTools.Services;

public sealed class AzureOcrService : IOcrService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly OcrPipelineLogger _pipelineLogger;

    public AzureOcrService(string endpoint, string apiKey)
    {
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _apiKey);

        _pipelineLogger = new OcrPipelineLogger(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "Logs"));
    }

    public async Task<OcrResult> ExtractTextAsync(Bitmap bitmap, string? debugOutputDirectory = null, string? preferredMode = null, string? captureProfile = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString();

        try
        {
            var rawText = await ExtractRawTextFromAzureAsync(bitmap);
            var normalizedText = rawText.Replace("\r", string.Empty).Trim();

            // Create a fake OcrAttempt using the raw text from Azure
            var attempt = new OcrAttempt("azure-cv", rawText, normalizedText, 1.0f);

            var scoredAttempt = OcrParser.EvaluateAttempt(attempt, captureProfile);
            var bestAttempt = OcrParser.SelectBestAttempt(new[] { scoredAttempt }, preferredMode);
            var result = OcrParser.BuildResult(bestAttempt, preferredMode, captureProfile);

            if (!string.IsNullOrWhiteSpace(debugOutputDirectory))
            {
                Directory.CreateDirectory(debugOutputDirectory);
                var summaryBuilder = new StringBuilder();
                summaryBuilder.AppendLine($"Preferred mode: {preferredMode ?? OcrModeCatalog.Auto}");
                summaryBuilder.AppendLine($"Selected attempt: {result.UsedMode}");
                summaryBuilder.AppendLine($"Selected confidence: {result.Confidence:F4}");
                summaryBuilder.AppendLine($"Selected text: {result.NormalizedText}");
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine(rawText);

                File.WriteAllText(Path.Combine(debugOutputDirectory, "ocr_selected.txt"), result.NormalizedText);
                File.WriteAllText(Path.Combine(debugOutputDirectory, "ocr_attempts.txt"), summaryBuilder.ToString());
            }

            LogPipelineSuccess(requestId, captureProfile, preferredMode, bitmap.Width, bitmap.Height, totalStopwatch.ElapsedMilliseconds, result);
            return result;
        }
        catch (Exception ex)
        {
            LogPipelineFailure(requestId, captureProfile, preferredMode, bitmap.Width, bitmap.Height, totalStopwatch.ElapsedMilliseconds, ex);
            throw;
        }
    }

    private async Task<string> ExtractRawTextFromAzureAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var url = $"{_endpoint}/vision/v3.2/read/analyze";
        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        if (!response.Headers.TryGetValues("Operation-Location", out var locationValues))
        {
            throw new InvalidOperationException("Operation-Location header missing from Azure Computer Vision response.");
        }

        var operationLocation = locationValues.First();

        while (true)
        {
            await Task.Delay(1000); // Polling interval
            var resultResponse = await _httpClient.GetAsync(operationLocation);
            resultResponse.EnsureSuccessStatusCode();

            var jsonString = await resultResponse.Content.ReadAsStringAsync();
            using var jsonDocument = JsonDocument.Parse(jsonString);
            var root = jsonDocument.RootElement;

            if (root.TryGetProperty("status", out var statusProperty))
            {
                var status = statusProperty.GetString();
                if (status == "succeeded")
                {
                    return ExtractTextFromJson(root);
                }
                if (status == "failed")
                {
                    throw new Exception("Azure Computer Vision read operation failed.");
                }
            }
        }
    }

    private record OcrLine(string Text, double CenterY, double LeftX, double LineHeight);

    private static string ExtractTextFromJson(JsonElement root)
    {
        var extractedLines = new List<OcrLine>();

        if (root.TryGetProperty("analyzeResult", out var analyzeResult) &&
            analyzeResult.TryGetProperty("readResults", out var readResults))
        {
            foreach (var readResult in readResults.EnumerateArray())
            {
                if (readResult.TryGetProperty("lines", out var lines))
                {
                    foreach (var line in lines.EnumerateArray())
                    {
                        if (line.TryGetProperty("text", out var textToken) && line.TryGetProperty("boundingBox", out var bbToken))
                        {
                            var text = textToken.GetString() ?? string.Empty;
                            var boxArray = bbToken.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                            
                            if (boxArray.Length >= 8)
                            {
                                var topY = Math.Min(boxArray[1], boxArray[3]);
                                var bottomY = Math.Max(boxArray[5], boxArray[7]);
                                var leftX = Math.Min(boxArray[0], boxArray[6]);
                                
                                var centerY = (topY + bottomY) / 2.0;
                                var height = bottomY - topY;
                                
                                extractedLines.Add(new OcrLine(text, centerY, leftX, height));
                            }
                        }
                    }
                }
            }
        }

        if (extractedLines.Count == 0) return string.Empty;

        var sortedLines = extractedLines.OrderBy(l => l.CenterY).ToList();
        var groupedLines = new List<List<OcrLine>>();
        var currentGroup = new List<OcrLine> { sortedLines[0] };
        groupedLines.Add(currentGroup);

        for (int i = 1; i < sortedLines.Count; i++)
        {
            var line = sortedLines[i];
            var currentGroupAvgY = currentGroup.Average(l => l.CenterY);
            var maxLineHeight = Math.Max(line.LineHeight, currentGroup.Max(l => l.LineHeight));
            var tolerance = maxLineHeight * 0.5;

            if (Math.Abs(line.CenterY - currentGroupAvgY) <= tolerance)
            {
                currentGroup.Add(line);
            }
            else
            {
                currentGroup = new List<OcrLine> { line };
                groupedLines.Add(currentGroup);
            }
        }

        var stringBuilder = new StringBuilder();
        foreach (var group in groupedLines)
        {
            var sortedGroup = group.OrderBy(l => l.LeftX).ToList();
            var lineText = string.Join(" ", sortedGroup.Select(l => l.Text));
            stringBuilder.AppendLine(lineText);
        }

        return stringBuilder.ToString();
    }

    private void LogPipelineSuccess(string requestId, string? captureProfile, string? preferredMode, int width, int height, long elapsedMs, OcrResult result)
    {
        var timings = new Dictionary<string, double> { { "total", elapsedMs } };
        var entry = new OcrPipelineLogEntry
        {
            RequestId = requestId,
            TimestampUtc = DateTime.UtcNow,
            Status = "completed",
            CaptureProfile = captureProfile,
            PreferredMode = preferredMode,
            ImageWidth = width,
            ImageHeight = height,
            AttemptCount = 1,
            ResultMode = result.UsedMode,
            ResultConfidence = result.Confidence,
            ResultTextLength = result.NormalizedText?.Length ?? 0,
            StageTimingsMs = timings
        };
        _pipelineLogger.Write(entry);
    }

    private void LogPipelineFailure(string requestId, string? captureProfile, string? preferredMode, int width, int height, long elapsedMs, Exception ex)
    {
         var timings = new Dictionary<string, double> { { "total", elapsedMs } };
         var entry = new OcrPipelineLogEntry
         {
             RequestId = requestId,
             TimestampUtc = DateTime.UtcNow,
             Status = "failed",
             CaptureProfile = captureProfile,
             PreferredMode = preferredMode,
             ImageWidth = width,
             ImageHeight = height,
             Exception = ex.ToString(),
             StageTimingsMs = timings
         };
         _pipelineLogger.Write(entry);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
