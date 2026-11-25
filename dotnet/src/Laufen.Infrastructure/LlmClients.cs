using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Laufen.Domain;

namespace Laufen.Infrastructure;

/// <summary>
/// Deterministic stub LLM client for offline tests, self-correction verification, and dry-run previews.
/// </summary>
public sealed class StubLlmClient : ILlmClient
{
    private readonly Queue<LlmResponse> _queuedResponses;
    private readonly Func<LlmRequest, LlmResponse>? _responseFactory;
    private readonly List<LlmRequest> _recordedRequests = [];
    private readonly string _model;

    public StubLlmClient(IEnumerable<string> rawJsonResponses, string model = "stub-model")
    {
        ArgumentNullException.ThrowIfNull(rawJsonResponses);
        _model = model;
        _queuedResponses = new Queue<LlmResponse>(
            rawJsonResponses.Select(text => new LlmResponse(true, text, null, model, 0.01)));
    }

    public StubLlmClient(IEnumerable<LlmResponse> responses, string model = "stub-model")
    {
        ArgumentNullException.ThrowIfNull(responses);
        _model = model;
        _queuedResponses = new Queue<LlmResponse>(responses);
    }

    public StubLlmClient(Func<LlmRequest, LlmResponse> responseFactory, string model = "stub-model")
    {
        ArgumentNullException.ThrowIfNull(responseFactory);
        _model = model;
        _queuedResponses = new Queue<LlmResponse>();
        _responseFactory = responseFactory;
    }

    public IReadOnlyList<LlmRequest> RecordedRequests => _recordedRequests;

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _recordedRequests.Add(request);

        if (_responseFactory is not null)
        {
            return Task.FromResult(_responseFactory(request));
        }

        if (_queuedResponses.Count == 0)
        {
            return Task.FromResult(new LlmResponse(
                false,
                string.Empty,
                "StubLlmClient queue exhausted.",
                _model,
                0.0));
        }

        var next = _queuedResponses.Count == 1
            ? _queuedResponses.Peek()
            : _queuedResponses.Dequeue();
        return Task.FromResult(next);
    }
}

/// <summary>
/// Executes the local Antigravity CLI (<c>agy</c>) in non-interactive print mode.
/// </summary>
public sealed partial class AgyLlmClient : ILlmClient
{
    private readonly string? _explicitBinaryPath;
    private readonly string? _model;
    private readonly TimeSpan _timeout;
    private readonly bool _useSandbox;

    [GeneratedRegex(@"file://(?<path>[^\s)\]]+\.(?:md|json|txt))", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactFileUriPattern();

    public AgyLlmClient(
        string? explicitBinaryPath = null,
        string? model = null,
        TimeSpan? timeout = null,
        bool useSandbox = true)
    {
        _explicitBinaryPath = string.IsNullOrWhiteSpace(explicitBinaryPath) ? null : explicitBinaryPath.Trim();
        var envModel = Environment.GetEnvironmentVariable("AGY_MODEL");
        _model = !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : (string.IsNullOrWhiteSpace(envModel) ? null : envModel.Trim());
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
        _useSandbox = useSandbox;
    }

    public string? ResolveBinaryPath()
    {
        if (!string.IsNullOrWhiteSpace(_explicitBinaryPath))
        {
            return File.Exists(_explicitBinaryPath) ? _explicitBinaryPath : null;
        }

        var envBin = Environment.GetEnvironmentVariable("AGY_BIN");
        if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin))
        {
            return envBin;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "agy");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            var localBin = Path.Combine(homeDir, ".local", "bin", "agy");
            if (File.Exists(localBin))
            {
                return localBin;
            }
        }

        return null;
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var binary = ResolveBinaryPath();
        if (binary is null)
        {
            return new LlmResponse(
                false,
                string.Empty,
                "agy CLI binary not found. Ensure 'agy' is installed or set AGY_BIN.",
                _model);
        }

        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(_timeout.TotalSeconds));
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in BuildArguments(timeoutSeconds))
        {
            startInfo.ArgumentList.Add(arg);
        }

        var fullPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? request.UserPrompt
            : $"System Instruction:\n{request.SystemPrompt}\n\nUser Request:\n{request.UserPrompt}";

        var sw = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new LlmResponse(false, string.Empty, "Failed to start agy process.", _model, sw.Elapsed.TotalSeconds);
            }

            await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                await stdin.WriteAsync(fullPrompt.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var rawStdout = await stdoutTask.ConfigureAwait(false);
            var rawStderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            if (process.ExitCode != 0)
            {
                var errMsg = string.IsNullOrWhiteSpace(rawStderr)
                    ? $"agy exited with code {process.ExitCode}."
                    : rawStderr.Trim();
                return new LlmResponse(false, rawStdout, errMsg, _model ?? "agy-default", sw.Elapsed.TotalSeconds);
            }

            var resolved = InlineArtifactIfPresent(rawStdout);
            return new LlmResponse(true, resolved, null, _model ?? "agy-default", sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new LlmResponse(false, string.Empty, $"agy execution timed out or cancelled after {sw.Elapsed.TotalSeconds:F1}s.", _model, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            sw.Stop();
            return new LlmResponse(false, string.Empty, $"Failed to invoke agy: {ex.Message}", _model, sw.Elapsed.TotalSeconds);
        }
    }

    public IReadOnlyList<string> BuildArguments(int timeoutSeconds)
    {
        var args = new List<string>
        {
            "--print-timeout",
            string.Create(CultureInfo.InvariantCulture, $"{timeoutSeconds}s"),
            "--input-format",
            "text",
            "--output-format",
            "text",
            "--disable-slash-commands",
            "--dangerously-skip-permissions"
        };

        if (_useSandbox)
        {
            args.Add("--sandbox");
        }

        if (!string.IsNullOrWhiteSpace(_model))
        {
            args.Add("--model");
            args.Add(_model);
        }

        return args;
    }

    private static string InlineArtifactIfPresent(string rawStdout)
    {
        var match = ArtifactFileUriPattern().Match(rawStdout);
        if (match.Success)
        {
            var path = match.Groups["path"].Value;
            if (File.Exists(path))
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }

        return rawStdout;
    }
}

/// <summary>
/// Direct Google Gemini REST API client using <see cref="HttpClient"/>.
/// </summary>
public sealed class GeminiApiLlmClient : ILlmClient
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public GeminiApiLlmClient(
        string? apiKey = null,
        string? model = null,
        HttpClient? httpClient = null)
    {
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey.Trim()
            : (Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty);
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model.Trim();
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new LlmResponse(false, string.Empty, "GEMINI_API_KEY is not set.", _model);
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
        var contents = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            contents.Add(new { role = "user", parts = new[] { new { text = $"SYSTEM: {request.SystemPrompt}" } } });
            contents.Add(new { role = "model", parts = new[] { new { text = "Understood." } } });
        }

        contents.Add(new { role = "user", parts = new[] { new { text = request.UserPrompt } } });

        var payload = new
        {
            contents,
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "application/json"
            }
        };

        var sw = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new LlmResponse(false, string.Empty, $"Gemini API HTTP {(int)response.StatusCode}: {body}", _model, sw.Elapsed.TotalSeconds);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                return new LlmResponse(false, string.Empty, "No candidates returned from Gemini API.", _model, sw.Elapsed.TotalSeconds);
            }

            var sb = new StringBuilder();
            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textProp))
                {
                    sb.Append(textProp.GetString());
                }
            }

            return new LlmResponse(true, sb.ToString(), null, _model, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            sw.Stop();
            return new LlmResponse(false, string.Empty, $"Gemini API request failed: {ex.Message}", _model, sw.Elapsed.TotalSeconds);
        }
    }
}

public static class LlmClientFactory
{
    public static ILlmClient Create(string provider, string? model = null, string? stubResponseJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return provider.Trim().ToLowerInvariant() switch
        {
            "stub" or "mock" or "test" => new StubLlmClient(
                stubResponseJson is not null ? [stubResponseJson] : [],
                string.IsNullOrWhiteSpace(model) ? "stub-model" : model),
            "agy" or "cli" or "antigravity" => new AgyLlmClient(model: model),
            "gemini" or "api" or "google" => new GeminiApiLlmClient(model: model),
            _ => throw new ArgumentException($"Unsupported LLM provider '{provider}'. Expected stub, agy, or gemini.", nameof(provider))
        };
    }
}
