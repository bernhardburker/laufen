using System.Globalization;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Zero-dependency Open-Meteo 5-day weather forecast client and pure JSON parser.
/// </summary>
public static class OpenMeteoWeatherClient
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static WeatherForecastDossier ParseOpenMeteoJson(
        string json,
        DateOnly refDate,
        string? locationLabel = null,
        DateTimeOffset? fetchedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var doc = JsonDocument.Parse(json);
        var daily = doc.RootElement.GetProperty("daily");

        var times = daily.GetProperty("time");
        var tempMax = daily.GetProperty("temperature_2m_max");
        var tempMin = daily.GetProperty("temperature_2m_min");
        var precip = daily.GetProperty("precipitation_sum");
        var windGusts = daily.GetProperty("wind_gusts_10m_max");
        var weatherCodes = daily.TryGetProperty("weather_code", out var wc) ? wc : default;

        var count = times.GetArrayLength();
        var days = new List<DailyWeatherForecast>(count);
        var endWindow = refDate.AddDays(5);

        for (var i = 0; i < count; i++)
        {
            var dateStr = times[i].GetString();
            if (!DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            if (date < refDate || date > endWindow)
            {
                continue;
            }

            var code = weatherCodes.ValueKind == JsonValueKind.Array && i < weatherCodes.GetArrayLength()
                ? weatherCodes[i].GetInt32()
                : 0;

            days.Add(new DailyWeatherForecast
            {
                Date = date,
                TempMaxC = Math.Round(tempMax[i].GetDouble(), 1, MidpointRounding.ToEven),
                TempMinC = Math.Round(tempMin[i].GetDouble(), 1, MidpointRounding.ToEven),
                PrecipitationMm = Math.Round(precip[i].GetDouble(), 1, MidpointRounding.ToEven),
                MaxWindGustKmh = Math.Round(windGusts[i].GetDouble(), 1, MidpointRounding.ToEven),
                Summary = MapWmoWeatherCode(code)
            });
        }

        return new WeatherForecastDossier
        {
            Source = "open-meteo",
            FetchedAt = fetchedAt,
            LocationLabel = locationLabel,
            Days = days
        };
    }

    public static async Task<WeatherForecastDossier?> FetchAsync(
        double latitude,
        double longitude,
        DateOnly refDate,
        string? locationLabel = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude:F4}&longitude={longitude:F4}&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,wind_gusts_10m_max&timezone=auto&forecast_days=6");

        try
        {
            var client = httpClient ?? SharedHttpClient;
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseOpenMeteoJson(body, refDate, locationLabel, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static string MapWmoWeatherCode(int code) => code switch
    {
        0 => "Clear",
        1 or 2 or 3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 or 56 or 57 => "Drizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "Rain",
        71 or 73 or 75 or 77 or 85 or 86 => "Snow",
        >= 95 => "Storm",
        _ => "Mixed"
    };
}
