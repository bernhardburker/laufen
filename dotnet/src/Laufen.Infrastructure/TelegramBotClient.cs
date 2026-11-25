using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laufen.Domain;

namespace Laufen.Infrastructure;

public interface ITelegramBotClient
{
    Task<long?> SendMessageAsync(TelegramOutgoingMessage message, CancellationToken cancellationToken = default);
    Task AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelegramIncomingUpdate>> GetUpdatesAsync(long? offset = null, int timeoutSeconds = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP client wrapper around the Telegram Bot API using injectable <see cref="HttpClient"/>.
/// Never logs or embeds bot tokens in exception messages.
/// </summary>
public sealed class TelegramBotHttpClient : ITelegramBotClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _botToken;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public TelegramBotHttpClient(string botToken, HttpClient? httpClient = null, string baseUrl = "https://api.telegram.org")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _botToken = botToken.Trim();
        _httpClient = httpClient ?? SharedHttpClient;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<long?> SendMessageAsync(TelegramOutgoingMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = new TelegramSendMessagePayload
        {
            ChatId = message.ChatId,
            Text = message.Text,
            ParseMode = message.ParseMode,
            ReplyMarkup = message.ReplyMarkup
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildMethodUri("sendMessage"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess("sendMessage", response, body);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("result", out var resultEl)
            && resultEl.ValueKind == JsonValueKind.Object
            && resultEl.TryGetProperty("message_id", out var msgIdEl)
            && msgIdEl.TryGetInt64(out var messageId))
        {
            return messageId;
        }

        return null;
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackQueryId);

        var payload = new
        {
            callback_query_id = callbackQueryId,
            text
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildMethodUri("answerCallbackQuery"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess("answerCallbackQuery", response, body);
    }

    public async Task<IReadOnlyList<TelegramIncomingUpdate>> GetUpdatesAsync(
        long? offset = null,
        int timeoutSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var queryParts = new List<string>();
        if (offset.HasValue)
        {
            queryParts.Add(string.Create(CultureInfo.InvariantCulture, $"offset={offset.Value}"));
        }

        if (timeoutSeconds > 0)
        {
            queryParts.Add(string.Create(CultureInfo.InvariantCulture, $"timeout={timeoutSeconds}"));
        }

        var query = queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : string.Empty;
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildMethodUri("getUpdates" + query));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess("getUpdates", response, body);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("result", out var resultEl) || resultEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var updates = new List<TelegramIncomingUpdate>();
        foreach (var item in resultEl.EnumerateArray())
        {
            if (!item.TryGetProperty("update_id", out var updateIdEl) || !updateIdEl.TryGetInt64(out var updateId))
            {
                continue;
            }

            TelegramUpdateMessage? message = null;
            if (item.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
            {
                var msgId = msgEl.TryGetProperty("message_id", out var mIdEl) && mIdEl.TryGetInt64(out var mId) ? mId : 0L;
                var chatId = ExtractChatId(msgEl) ?? string.Empty;
                var text = msgEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString()
                    : null;
                var dateUnix = msgEl.TryGetProperty("date", out var dateEl) && dateEl.TryGetInt64(out var unix) ? unix : 0L;
                if (!string.IsNullOrEmpty(chatId))
                {
                    message = new TelegramUpdateMessage
                    {
                        MessageId = msgId,
                        ChatId = chatId,
                        Text = text,
                        UnixTimestamp = dateUnix
                    };
                }
            }

            TelegramCallbackQuery? callback = null;
            if (item.TryGetProperty("callback_query", out var cbEl) && cbEl.ValueKind == JsonValueKind.Object)
            {
                var cbId = cbEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var cbData = cbEl.TryGetProperty("data", out var dataEl) ? dataEl.GetString() : null;
                var chatId = cbEl.TryGetProperty("message", out var cbMsgEl) && cbMsgEl.ValueKind == JsonValueKind.Object
                    ? ExtractChatId(cbMsgEl)
                    : null;
                chatId ??= cbEl.TryGetProperty("from", out var fromEl) && fromEl.ValueKind == JsonValueKind.Object
                    && fromEl.TryGetProperty("id", out var fromIdEl) && fromIdEl.TryGetInt64(out var fid)
                        ? fid.ToString(CultureInfo.InvariantCulture)
                        : null;
                var msgText = cbEl.TryGetProperty("message", out var msgObj)
                    && msgObj.TryGetProperty("text", out var mTextEl)
                    && mTextEl.ValueKind == JsonValueKind.String
                        ? mTextEl.GetString()
                        : null;

                if (!string.IsNullOrWhiteSpace(cbId) && !string.IsNullOrWhiteSpace(cbData) && !string.IsNullOrWhiteSpace(chatId))
                {
                    callback = new TelegramCallbackQuery
                    {
                        Id = cbId,
                        ChatId = chatId,
                        Data = cbData,
                        MessageText = msgText
                    };
                }
            }

            updates.Add(new TelegramIncomingUpdate
            {
                UpdateId = updateId,
                Message = message,
                CallbackQuery = callback
            });
        }

        return updates;
    }

    private Uri BuildMethodUri(string methodAndQuery)
    {
        return new Uri($"{_baseUrl}/bot{_botToken}/{methodAndQuery}");
    }

    private static string? ExtractChatId(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("chat", out var chatEl)
            || chatEl.ValueKind != JsonValueKind.Object
            || !chatEl.TryGetProperty("id", out var idEl))
        {
            return null;
        }

        return idEl.ValueKind switch
        {
            JsonValueKind.Number when idEl.TryGetInt64(out var idNum) => idNum.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => idEl.GetString(),
            _ => null
        };
    }

    private static void EnsureSuccess(string method, HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Telegram API '{method}' failed with HTTP {(int)response.StatusCode}: {Truncate(body, 200)}"));
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("ok", out var okEl) && !okEl.GetBoolean())
        {
            var desc = doc.RootElement.TryGetProperty("description", out var descEl)
                ? descEl.GetString()
                : "unknown error";
            throw new InvalidOperationException($"Telegram API '{method}' returned ok=false: {desc}");
        }
    }

    private static string Truncate(string value, int maxLen)
    {
        return value.Length <= maxLen ? value : value[..maxLen] + "...";
    }

    private sealed record TelegramSendMessagePayload
    {
        [JsonPropertyName("chat_id")]
        public required string ChatId { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("parse_mode")]
        public required string ParseMode { get; init; }

        [JsonPropertyName("reply_markup")]
        public TelegramReplyMarkup? ReplyMarkup { get; init; }
    }
}
