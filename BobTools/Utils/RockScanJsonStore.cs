using System.Globalization;
using System.IO;
using System.Text.Json;
using BobTools.Models;

namespace BobTools.Utils;

internal static class RockScanJsonStore
{
    public static string Save(RockScan rockScan)
    {
        var folder = !string.IsNullOrWhiteSpace(rockScan.DebugFolderPath)
            ? rockScan.DebugFolderPath!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BobTools",
                "RockScans",
                "Json");

        Directory.CreateDirectory(folder);

        var safeMaterial = MakeSafeFileSegment(string.IsNullOrWhiteSpace(rockScan.Material) ? "unknown" : rockScan.Material);
        var fileName = !string.IsNullOrWhiteSpace(rockScan.DebugFolderPath)
            ? "rock_scan.json"
            : $"{rockScan.TimestampUtc:yyyyMMdd_HHmmss_fff}_{safeMaterial}.json";
        var filePath = Path.Combine(folder, fileName);

        var payload = new
        {
            ID = rockScan.Id,
            SESSIONID = rockScan.SessionId,
            TIMESTAMP = rockScan.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            LOCATION = rockScan.Location,
            MATERIAL = rockScan.Material,
            MASS = rockScan.Mass,
            RESISTANCE = rockScan.Resistance,
            INSTABILITY = rockScan.Instability,
            COMPOSITION = rockScan.Composition,
            Elements = rockScan.Elements.Select(entry => new
            {
                entry.Element,
                percentage = entry.Percentage,
                entry.Quality
            }).ToArray(),
            PARSE_WARNINGS = rockScan.ParseWarnings.ToArray(),
            SOURCE_TYPE = rockScan.SourceType,
            CAPTURE_PROFILE = rockScan.CaptureProfile,
            SOURCE_IMAGE_PATH = rockScan.SourceImagePath,
            DEBUG_FOLDER_PATH = rockScan.DebugFolderPath,
            SOURCE_TEXT = rockScan.SourceText
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
        return filePath;
    }

    private static string MakeSafeFileSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return sanitized.Replace(' ', '_');
    }
}