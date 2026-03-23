using System.IO;
using System.Text.Json;
using BobTools.Models;

namespace BobTools.Utils;

internal static class MiningContextCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> ShipOptions = new(LoadShipOptions);
    private static readonly Lazy<IReadOnlyList<string>> SystemOptions = new(LoadSystemOptions);
    private static readonly Lazy<IReadOnlyList<MiningLocationOption>> LocationOptions = new(LoadLocationOptions);

    public static IReadOnlyList<string> GetShipOptions()
    {
        return ShipOptions.Value;
    }

    public static IReadOnlyList<string> GetSystemOptions()
    {
        return SystemOptions.Value;
    }

    public static IReadOnlyList<MiningLocationOption> GetLocationOptions()
    {
        return LocationOptions.Value;
    }

    private static IReadOnlyList<string> LoadShipOptions()
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "data", "mining_ships.json");
        if (!File.Exists(dataPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var stream = File.OpenRead(dataPath);
            using var document = JsonDocument.Parse(stream);
            var items = GetRootArray(document.RootElement);
            if (items is null)
            {
                return Array.Empty<string>();
            }

            return items.Value
                .EnumerateArray()
                .Select(GetShipName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> LoadSystemOptions()
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "data", "star_systems.json");
        if (!File.Exists(dataPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var stream = File.OpenRead(dataPath);
            using var document = JsonDocument.Parse(stream);
            var items = GetRootArray(document.RootElement);
            if (items is null)
            {
                return Array.Empty<string>();
            }

            return items.Value
                .EnumerateArray()
                .Where(IsAvailableAndVisible)
                .Select(item => GetString(item, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<MiningLocationOption> LoadLocationOptions()
    {
        var planets = LoadJsonRows("planets.json");
        var moons = LoadJsonRows("moons.json");
        var stations = LoadJsonRows("space_stations.json");
        var pois = LoadJsonRows("poi.json");

        var planetLookup = planets
            .Where(IsAvailableAndVisible)
            .Select(row => new
            {
                Id = GetInt32(row, "id"),
                Name = GetString(row, "name"),
                SystemName = GetString(row, "star_system_name"),
                Code = GetString(row, "code")
            })
            .Where(node => node.Id > 0 && !string.IsNullOrWhiteSpace(node.Name) && !string.IsNullOrWhiteSpace(node.SystemName))
            .Select(node => new PlanetNode(node.Id, node.Name!, node.SystemName!, node.Code))
            .ToDictionary(node => node.Id);

        var locations = new List<MiningLocationOption>();

        foreach (var planet in planetLookup.Values.OrderBy(item => item.SystemName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            locations.Add(new MiningLocationOption
            {
                Key = $"planet:{planet.Id}",
                SystemName = planet.SystemName,
                DisplayName = planet.Name,
                DisplayPath = $"{planet.SystemName} / {planet.Name}",
                SearchText = BuildSearchText(planet.SystemName, planet.Name, planet.Code)
            });
        }

        foreach (var moon in moons.Where(IsAvailableAndVisible))
        {
            var systemName = GetString(moon, "star_system_name");
            var moonName = GetString(moon, "name");
            var moonCode = GetString(moon, "code");
            var moonId = GetInt32(moon, "id");
            var parentPlanetName = GetString(moon, "planet_name");
            var parentPlanetId = GetInt32(moon, "id_planet");

            if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(moonName) || moonId <= 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(parentPlanetName) && planetLookup.TryGetValue(parentPlanetId, out var parentPlanet))
            {
                parentPlanetName = parentPlanet.Name;
            }

            var displayPath = string.IsNullOrWhiteSpace(parentPlanetName)
                ? $"{systemName} / {moonName}"
                : $"{systemName} / {parentPlanetName} / {moonName}";

            locations.Add(new MiningLocationOption
            {
                Key = $"moon:{moonId}",
                SystemName = systemName,
                DisplayName = moonName,
                DisplayPath = displayPath,
                SearchText = BuildSearchText(systemName, parentPlanetName, moonName, moonCode)
            });
        }

        foreach (var station in stations.Where(IsAvailableAndVisible).Where(row => GetInt32(row, "is_decommissioned") == 0))
        {
            var systemName = GetString(station, "star_system_name");
            var stationId = GetInt32(station, "id");
            if (string.IsNullOrWhiteSpace(systemName) || stationId <= 0)
            {
                continue;
            }

            var locationName = FirstNonEmpty(
                GetString(station, "nickname"),
                GetString(station, "orbit_name"),
                GetString(station, "name"));

            if (string.IsNullOrWhiteSpace(locationName))
            {
                continue;
            }

            var planetName = GetString(station, "planet_name");
            var moonName = GetString(station, "moon_name");
            var displayPath = BuildStationPath(systemName, planetName, moonName, locationName);

            locations.Add(new MiningLocationOption
            {
                Key = $"station:{stationId}",
                SystemName = systemName,
                DisplayName = locationName,
                DisplayPath = displayPath,
                SearchText = BuildSearchText(
                    systemName,
                    planetName,
                    moonName,
                    locationName,
                    GetString(station, "name"),
                    GetString(station, "orbit_name"))
            });
        }

        foreach (var poi in pois.Where(IsAvailableAndVisible).Where(row => GetInt32(row, "is_decommissioned") == 0))
        {
            var systemName = GetString(poi, "star_system_name");
            var poiId = GetInt32(poi, "id");
            if (string.IsNullOrWhiteSpace(systemName) || poiId <= 0)
            {
                continue;
            }

            var locationName = FirstNonEmpty(
                GetString(poi, "nickname"),
                GetString(poi, "name"),
                GetString(poi, "outpost_name"),
                GetString(poi, "city_name"),
                GetString(poi, "space_station_name"));

            if (string.IsNullOrWhiteSpace(locationName))
            {
                continue;
            }

            var planetName = GetString(poi, "planet_name");
            var moonName = GetString(poi, "moon_name");
            var spaceStationName = GetString(poi, "space_station_name");
            var cityName = GetString(poi, "city_name");
            var outpostName = GetString(poi, "outpost_name");
            var orbitName = GetString(poi, "orbit_name");

            locations.Add(new MiningLocationOption
            {
                Key = $"poi:{poiId}",
                SystemName = systemName,
                DisplayName = locationName,
                DisplayPath = BuildPoiPath(systemName, planetName, moonName, spaceStationName, cityName, outpostName, orbitName, locationName),
                SearchText = BuildSearchText(
                    systemName,
                    planetName,
                    moonName,
                    spaceStationName,
                    cityName,
                    outpostName,
                    orbitName,
                    locationName,
                    GetString(poi, "name"),
                    GetString(poi, "nickname"),
                    GetString(poi, "type"),
                    GetString(poi, "subtype"))
            });
        }

        return locations
            .GroupBy(location => location.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(location => location.SystemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<JsonElement> LoadJsonRows(string fileName)
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "data", fileName);
        if (!File.Exists(dataPath))
        {
            return Array.Empty<JsonElement>();
        }

        try
        {
            using var stream = File.OpenRead(dataPath);
            using var document = JsonDocument.Parse(stream);
            var items = GetRootArray(document.RootElement);
            return items?.EnumerateArray().Select(item => item.Clone()).ToArray() ?? Array.Empty<JsonElement>();
        }
        catch (JsonException)
        {
            return Array.Empty<JsonElement>();
        }
    }

    private static JsonElement? GetRootArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
        {
            return dataElement;
        }

        return null;
    }

    private static string? GetShipName(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()?.Trim(),
            JsonValueKind.Object => GetString(element, "name"),
            _ => null
        };
    }

    private static bool IsAvailableAndVisible(JsonElement element)
    {
        return GetInt32(element, "is_available") == 1 && GetInt32(element, "is_visible") == 1;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString()?.Trim();
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static string BuildStationPath(string systemName, string? planetName, string? moonName, string locationName)
    {
        if (!string.IsNullOrWhiteSpace(moonName) && !string.IsNullOrWhiteSpace(planetName))
        {
            return $"{systemName} / {planetName} / {moonName} / {locationName}";
        }

        if (!string.IsNullOrWhiteSpace(planetName))
        {
            return $"{systemName} / {planetName} / {locationName}";
        }

        if (!string.IsNullOrWhiteSpace(moonName))
        {
            return $"{systemName} / {moonName} / {locationName}";
        }

        return $"{systemName} / {locationName}";
    }

    private static string BuildPoiPath(
        string systemName,
        string? planetName,
        string? moonName,
        string? spaceStationName,
        string? cityName,
        string? outpostName,
        string? orbitName,
        string locationName)
    {
        var segments = new List<string> { systemName };

        AppendDistinctSegment(segments, planetName);
        AppendDistinctSegment(segments, moonName);
        AppendDistinctSegment(segments, spaceStationName);
        AppendDistinctSegment(segments, cityName);
        AppendDistinctSegment(segments, outpostName);
        AppendDistinctSegment(segments, orbitName);
        AppendDistinctSegment(segments, locationName);

        return string.Join(" / ", segments);
    }

    private static string BuildSearchText(params string?[] parts)
    {
        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
    }

    private static void AppendDistinctSegment(ICollection<string> segments, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (segments.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        segments.Add(value);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record PlanetNode(int Id, string Name, string SystemName, string? Code);
}