using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Laufen.Domain;

namespace Laufen.Infrastructure;

public interface IIntervalsIcuClient
{
    Task<IReadOnlyList<IntervalsActivityEntry>> GetActivitiesAsync(
        string athleteId,
        DateOnly oldest,
        DateOnly newest,
        CancellationToken cancellationToken = default);

    Task<ActivityStreamData?> GetActivityStreamAsync(
        string activityId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IntervalsWellnessEntry>> GetWellnessAsync(
        string athleteId,
        DateOnly oldest,
        DateOnly newest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarEventEntry>> GetCalendarEventsAsync(
        string athleteId,
        DateOnly oldest,
        DateOnly newest,
        CancellationToken cancellationToken = default);

    Task<CalendarEventEntry> CreateCalendarEventAsync(
        string athleteId,
        IntervalsCalendarEventUpsertRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteCalendarEventAsync(
        string athleteId,
        long eventId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed REST client for Intervals.icu v1 API using Basic authentication (<c>API_KEY:&lt;key&gt;</c>).
/// </summary>
public sealed class IntervalsIcuHttpClient : IIntervalsIcuClient
{
    private const string DefaultStreamTypes = "time,heartrate,velocity_smooth,cadence,fixed_altitude";
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _apiKey;
    private readonly string _apiBaseUrl;
    private readonly HttpClient _httpClient;

    public IntervalsIcuHttpClient(
        string apiKey,
        string apiBaseUrl = "https://intervals.icu/api/v1",
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseUrl);

        _apiKey = apiKey.Trim();
        _apiBaseUrl = apiBaseUrl.Trim().TrimEnd('/');
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<IReadOnlyList<IntervalsActivityEntry>> GetActivitiesAsync(
        string athleteId,
        DateOnly oldest,
        DateOnly newest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteId);
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_apiBaseUrl}/athlete/{Uri.EscapeDataString(athleteId)}/activities?oldest={oldest:yyyy-MM-dd}&newest={newest:yyyy-MM-dd}");
        var json = await SendAsync(HttpMethod.Get, url, payloadJson: null, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? [] : StrictJsonLoader.Deserialize<List<IntervalsActivityEntry>>(json);
    }

    public async Task<ActivityStreamData?> GetActivityStreamAsync(
        string activityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_apiBaseUrl}/activity/{Uri.EscapeDataString(activityId.Trim())}/streams?types={DefaultStreamTypes}");
        var json = await SendAsync(HttpMethod.Get, url, payloadJson: null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            return StrictJsonLoader.Deserialize<ActivityStreamData>(json);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        IReadOnlyList<int?>? time = null;
        IReadOnlyList<double?>? heartrate = null;
        IReadOnlyList<double?>? velocity = null;
        IReadOnlyList<double?>? cadence = null;
        IReadOnlyList<double?>? fixedAltitude = null;
        IReadOnlyList<double?>? altitude = null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var typeProp)
                || !item.TryGetProperty("data", out var dataProp)
                || dataProp.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var typeName = typeProp.GetString();
            switch (typeName)
            {
                case "time":
                    time = ParseNullableIntArray(dataProp);
                    break;
                case "heartrate":
                    heartrate = ParseNullableDoubleArray(dataProp);
                    break;
                case "velocity_smooth":
                    velocity = ParseNullableDoubleArray(dataProp);
                    break;
                case "cadence":
                    cadence = ParseNullableDoubleArray(dataProp);
                    break;
                case "fixed_altitude":
                    fixedAltitude = ParseNullableDoubleArray(dataProp);
                    break;
                case "altitude":
                    altitude = ParseNullableDoubleArray(dataProp);
                    break;
            }
        }

        return new ActivityStreamData
        {
            Time = time,
            Heartrate = heartrate,
            VelocitySmooth = velocity,
            Cadence = cadence,
            FixedAltitude = fixedAltitude,
            Altitude = altitude
        };
    }

    public async Task<IReadOnlyList<IntervalsWellnessEntry>> GetWellnessAsync(
        string athleteId,
        DateOnly oldest,
        DateOnly newest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteId);
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_apiBaseUrl}/athlete/{Uri.EscapeDataString(athleteId)}/wellness?oldest={oldest:yyyy-MM-dd}&newest={newest:yyyy-MM-dd}");
        var json = await SendAsync(HttpMethod.Get, url, payloadJson: null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            var single = StrictJsonLoader.Deserialize<IntervalsWellnessEntry>(json);
            return [single];
        }

        return StrictJsonLoader.Deserialize<List<IntervalsWellnessEntry>>(json);
    }

    public async Task<IReadOnlyList<CalendarEventEntry>> GetCalendarEventsAsync(
        string athleteId,
        DateOnly oldest,
        DateOnly newest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteId);
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_apiBaseUrl}/athlete/{Uri.EscapeDataString(athleteId)}/events?oldest={oldest:yyyy-MM-dd}&newest={newest:yyyy-MM-dd}");
        var json = await SendAsync(HttpMethod.Get, url, payloadJson: null, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? [] : StrictJsonLoader.Deserialize<List<CalendarEventEntry>>(json);
    }

    public async Task<CalendarEventEntry> CreateCalendarEventAsync(
        string athleteId,
        IntervalsCalendarEventUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteId);
        ArgumentNullException.ThrowIfNull(request);

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_apiBaseUrl}/athlete/{Uri.EscapeDataString(athleteId)}/events");
        var payloadJson = JsonSerializer.Serialize(request);
        var body = await SendAsync(HttpMethod.Post, url, payloadJson, cancellationToken).ConfigureAwait(false);
        return StrictJsonLoader.Deserialize<CalendarEventEntry>(body);
    }

    public async Task<bool> DeleteCalendarEventAsync(
        string athleteId,
        long eventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(athleteId);

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_apiBaseUrl}/athlete/{Uri.EscapeDataString(athleteId)}/events/{eventId}");

        using var request = BuildRequest(HttpMethod.Delete, url, payloadJson: null);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Intervals.icu DELETE failed ({(int)response.StatusCode}): {errBody}");
        }

        return true;
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string url,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(method, url, payloadJson);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Intervals.icu {method.Method} {url} failed ({(int)response.StatusCode}): {body}");
        }

        return body;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string? payloadJson)
    {
        var request = new HttpRequestMessage(method, new Uri(url));
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"API_KEY:{_apiKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LaufenDotNet/1.0");

        if (payloadJson is not null)
        {
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static List<int?> ParseNullableIntArray(JsonElement array)
    {
        var list = new List<int?>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            list.Add(el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val) ? val : null);
        }

        return list;
    }

    private static List<double?> ParseNullableDoubleArray(JsonElement array)
    {
        var list = new List<double?>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            list.Add(el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var val) ? val : null);
        }

        return list;
    }
}

/// <summary>
/// Executes a <see cref="CalendarSyncPlan"/> against Intervals.icu with staging safety gates and dry-run protection.
/// </summary>
public static class IntervalsCalendarSyncExecutor
{
    public static async Task<CalendarSyncExecutionResult> ExecuteAsync(
        CalendarSyncPlan plan,
        AthleteRegistryEntry entry,
        IIntervalsIcuClient client,
        bool allowProductionWrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(client);

        var retainedAutoIds = plan.Actions
            .Where(a => string.Equals(a.Kind, "skip_unchanged", StringComparison.Ordinal)
                && string.Equals(a.Ownership, "auto", StringComparison.Ordinal)
                && a.ExistingEventId.HasValue)
            .Select(a => a.ExistingEventId!.Value)
            .Distinct()
            .ToList();

        if (plan.IsDryRun)
        {
            return new CalendarSyncExecutionResult
            {
                Plan = plan,
                Applied = false,
                DeletedEventIds = [],
                CreatedEventIds = [],
                RetainedAutoEventIds = retainedAutoIds,
                ResultingAutoEventIds = retainedAutoIds,
                Errors = []
            };
        }

        var permission = StagingSafetyGuard.EvaluateWritePermission(plan.AthleteSlug, entry, allowProductionWrite);
        if (!permission.Allowed)
        {
            return new CalendarSyncExecutionResult
            {
                Plan = plan,
                Applied = false,
                DeletedEventIds = [],
                CreatedEventIds = [],
                RetainedAutoEventIds = retainedAutoIds,
                ResultingAutoEventIds = retainedAutoIds,
                Errors = [permission.Reason]
            };
        }

        var deletedIds = new List<long>();
        var createdIds = new List<long>();
        var resultingAutoIds = new HashSet<long>(retainedAutoIds);
        var errors = new List<string>();

        foreach (var action in plan.Actions.Where(a => string.Equals(a.Kind, "delete", StringComparison.Ordinal)))
        {
            if (!action.ExistingEventId.HasValue)
            {
                continue;
            }

            try
            {
                var ok = await client.DeleteCalendarEventAsync(
                    plan.IntervalsAthleteId,
                    action.ExistingEventId.Value,
                    cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    deletedIds.Add(action.ExistingEventId.Value);
                }
                else
                {
                    errors.Add($"Delete event {action.ExistingEventId.Value} returned false.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add($"Delete event {action.ExistingEventId.Value} failed: {ex.Message}");
            }
        }

        foreach (var action in plan.Actions.Where(a => string.Equals(a.Kind, "create", StringComparison.Ordinal)))
        {
            if (action.Request is null)
            {
                continue;
            }

            try
            {
                var created = await client.CreateCalendarEventAsync(
                    plan.IntervalsAthleteId,
                    action.Request,
                    cancellationToken).ConfigureAwait(false);
                createdIds.Add(created.Id);
                if (string.Equals(action.Ownership, "auto", StringComparison.Ordinal))
                {
                    resultingAutoIds.Add(created.Id);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add($"Create event '{action.Request.Name}' on {action.Date:yyyy-MM-dd} failed: {ex.Message}");
            }
        }

        return new CalendarSyncExecutionResult
        {
            Plan = plan,
            Applied = errors.Count == 0,
            DeletedEventIds = deletedIds,
            CreatedEventIds = createdIds,
            RetainedAutoEventIds = retainedAutoIds,
            ResultingAutoEventIds = [.. resultingAutoIds.OrderBy(id => id)],
            Errors = errors
        };
    }
}
