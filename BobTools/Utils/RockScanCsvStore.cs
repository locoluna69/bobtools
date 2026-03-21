using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using BobTools.Models;

namespace BobTools.Utils;

internal static class RockScanCsvStore
{
    private const string CurrentHeader = "id,session_id,timestamp_utc,rsi_handle,ship,star_system,location_key,location_name,location_path,location,source_type,capture_profile,material,mass,resistance,instability,composition,elements_count,elements_json,parse_warnings_json,source_image_path,debug_folder_path,source_text";
    private const string LegacyHeader = "id,session_id,timestamp_utc,location,source_type,capture_profile,material,mass,resistance,instability,composition,elements_count,elements_json,parse_warnings_json,source_image_path,debug_folder_path,source_text";

    public static string Append(RockScan rockScan)
    {
        var csvPath = GetCsvPath();
        EnsureCurrentSchema(csvPath);
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
        EnsureCurrentSchema(csvPath);

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
        return CurrentHeader;
    }

    private static string BuildRow(RockScan rockScan)
    {
        return string.Join(",",
            Escape(rockScan.Id),
            Escape(rockScan.SessionId),
            Escape(rockScan.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
            Escape(rockScan.RsiHandle),
            Escape(rockScan.Ship),
            Escape(rockScan.StarSystem),
            Escape(rockScan.LocationKey),
            Escape(rockScan.LocationName),
            Escape(rockScan.LocationPath),
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

    private static void EnsureCurrentSchema(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            return;
        }

        var lines = File.ReadAllLines(csvPath).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var normalizedHeader = NormalizeHeader(lines[0]);
        if (string.Equals(normalizedHeader, CurrentHeader, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(normalizedHeader, LegacyHeader, StringComparison.Ordinal))
        {
            return;
        }

        lines[0] = CurrentHeader;
        for (var index = 1; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            lines[index] = UpgradeLegacyRow(lines[index]);
        }

        File.WriteAllLines(csvPath, lines, Encoding.UTF8);
    }

    private static string UpgradeLegacyRow(string line)
    {
        var columns = SplitCsvLine(line);
        while (columns.Count < 17)
        {
            columns.Add(string.Empty);
        }

        var upgradedColumns = new[]
        {
            columns[0],
            columns[1],
            columns[2],
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            columns[3],
            columns[4],
            columns[5],
            columns[6],
            columns[7],
            columns[8],
            columns[9],
            columns[10],
            columns[11],
            columns[12],
            columns[13],
            columns[14],
            columns[15],
            columns[16]
        };

        return string.Join(",", upgradedColumns.Select(Escape));
    }

    private static string NormalizeHeader(string header)
    {
        return header.TrimStart('\uFEFF');
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