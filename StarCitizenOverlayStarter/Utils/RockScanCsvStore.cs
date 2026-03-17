using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Utils;

internal static class RockScanCsvStore
{
    public static string Append(RockScan rockScan)
    {
        var csvPath = GetCsvPath();
        var writeHeader = !File.Exists(csvPath);

        using var writer = new StreamWriter(csvPath, append: true, Encoding.UTF8);

        if (writeHeader)
        {
            writer.WriteLine(GetHeader());
        }

        writer.WriteLine(BuildRow(rockScan));
        return csvPath;
    }

    public static string Upsert(RockScan rockScan)
    {
        var csvPath = GetCsvPath();

        if (!File.Exists(csvPath))
        {
            return Append(rockScan);
        }

        var lines = File.ReadAllLines(csvPath).ToList();
        if (lines.Count == 0)
        {
            lines.Add(GetHeader());
            lines.Add(BuildRow(rockScan));
            File.WriteAllLines(csvPath, lines, Encoding.UTF8);
            return csvPath;
        }

        var updated = false;
        for (var index = 1; index < lines.Count; index++)
        {
            var columns = SplitCsvLine(lines[index]);
            if (columns.Count == 0)
            {
                continue;
            }

            if (!string.Equals(columns[0], rockScan.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines[index] = BuildRow(rockScan);
            updated = true;
            break;
        }

        if (!updated)
        {
            lines.Add(BuildRow(rockScan));
        }

        File.WriteAllLines(csvPath, lines, Encoding.UTF8);
        return csvPath;
    }

    private static string GetCsvPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "RockScans");

        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "rock_scans.csv");
    }

    private static string GetHeader()
    {
        return "id,session_id,timestamp_utc,location,source_type,capture_profile,material,mass,resistance,instability,composition,elements_count,elements_json,parse_warnings_json,source_image_path,debug_folder_path,source_text";
    }

    private static string BuildRow(RockScan rockScan)
    {
        return string.Join(",",
            Escape(rockScan.Id),
            Escape(rockScan.SessionId),
            Escape(rockScan.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
            Escape(rockScan.Location),
            Escape(rockScan.SourceType),
            Escape(rockScan.CaptureProfile),
            Escape(rockScan.Material),
            Escape(rockScan.Mass?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(rockScan.Resistance?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(rockScan.Instability?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(rockScan.Composition?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(rockScan.Elements.Count.ToString(CultureInfo.InvariantCulture)),
            Escape(BuildElementsJson(rockScan)),
            Escape(BuildParseWarningsJson(rockScan)),
            Escape(rockScan.SourceImagePath ?? string.Empty),
            Escape(rockScan.DebugFolderPath ?? string.Empty),
            Escape(rockScan.SourceText.Replace("\r", " ").Replace("\n", " | ")));
    }

    private static string BuildElementsJson(RockScan rockScan)
    {
        return JsonSerializer.Serialize(rockScan.Elements.Select(entry => new
        {
            entry.Element,
            percentage = entry.Percentage,
            entry.Quality
        }));
    }

    private static string BuildParseWarningsJson(RockScan rockScan)
    {
        return JsonSerializer.Serialize(rockScan.ParseWarnings);
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        values.Add(builder.ToString());
        return values;
    }
}