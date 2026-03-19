using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace BobTools.Services;

public sealed partial class RsiProfileImageService : IRsiProfileImageService
{
    private static readonly Uri RsiBaseUri = new("https://robertsspaceindustries.com");
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly string _cacheRootPath;

    public RsiProfileImageService(string cacheRootPath)
    {
        _cacheRootPath = cacheRootPath;
    }

    public async Task<string?> GetProfileImagePathAsync(string rsiHandle, CancellationToken cancellationToken = default)
    {
        var normalizedHandle = rsiHandle?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedHandle))
        {
            return null;
        }

        Directory.CreateDirectory(_cacheRootPath);

        var safeHandle = string.Concat(normalizedHandle.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var cachedFile = Directory.GetFiles(_cacheRootPath, safeHandle + ".*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => !string.Equals(Path.GetExtension(path), ".tmp", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(cachedFile) && File.Exists(cachedFile))
        {
            return cachedFile;
        }

        var profileUrl = $"https://robertsspaceindustries.com/en/citizens/{Uri.EscapeDataString(normalizedHandle)}";
        using var response = await HttpClient.GetAsync(profileUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var imageUrl = ExtractProfileImageUrl(html);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var resolvedImageUri = ResolveImageUri(imageUrl);
        if (resolvedImageUri is null)
        {
            return null;
        }

        var extension = Path.GetExtension(resolvedImageUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var outputPath = Path.Combine(_cacheRootPath, safeHandle + extension);
        using var imageResponse = await HttpClient.GetAsync(resolvedImageUri, cancellationToken);
        if (!imageResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var tempPath = outputPath + ".tmp";
        await using (var outputStream = File.Create(tempPath))
        await using (var inputStream = await imageResponse.Content.ReadAsStreamAsync(cancellationToken))
        {
            await inputStream.CopyToAsync(outputStream, cancellationToken);
        }

        File.Move(tempPath, outputPath, overwrite: true);
        return outputPath;
    }

    private static string? ExtractProfileImageUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var profileBlockImageMatch = ProfileBlockImageRegex().Match(html);
        if (profileBlockImageMatch.Success)
        {
            return System.Net.WebUtility.HtmlDecode(profileBlockImageMatch.Groups["url"].Value);
        }

        var relativeProfileMatch = RelativeProfileImageRegex().Match(html);
        if (relativeProfileMatch.Success)
        {
            return relativeProfileMatch.Groups["url"].Value;
        }

        var profileMatch = ProfileImageRegex().Match(html);
        if (profileMatch.Success)
        {
            return profileMatch.Value;
        }

        var ogImageMatch = OgImageRegex().Match(html);
        if (ogImageMatch.Success)
        {
            return System.Net.WebUtility.HtmlDecode(ogImageMatch.Groups["url"].Value);
        }

        return null;
    }

    private static Uri? ResolveImageUri(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (imageUrl.StartsWith("//", StringComparison.Ordinal))
        {
            imageUrl = "https:" + imageUrl;
        }

        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        if (Uri.TryCreate(RsiBaseUri, imageUrl, out var relativeUri))
        {
            return relativeUri;
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BobTools/1.0");
        return httpClient;
    }

    [GeneratedRegex("https://robertsspaceindustries\\.com/media/[^\\\"'\\s<>]+/heap_infobox/[^\\\"'\\s<>]+\\.(?:png|jpg|jpeg|webp)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileImageRegex();

    [GeneratedRegex("(?<url>/media/[^\\\"'\\s<>]+/heap_infobox/[^\\\"'\\s<>]+\\.(?:png|jpg|jpeg|webp))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeProfileImageRegex();

    [GeneratedRegex("<div\\s+class=\"profile left-col\">.*?<div\\s+class=\"thumb\">\\s*<img\\s+src=\"(?<url>[^\"]+\\.(?:png|jpg|jpeg|webp))\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ProfileBlockImageRegex();

    [GeneratedRegex("<meta[^>]+property=[\\\"']og:image[\\\"'][^>]+content=[\\\"'](?<url>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OgImageRegex();
}