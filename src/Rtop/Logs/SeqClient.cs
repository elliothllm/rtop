using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtop.Logs;

public sealed class SeqOptions
{
    [JsonPropertyName("url")] public string Url { get; set; } = "http://localhost:5341";

    /// <summary>Leave both blank if your Seq runs unauthenticated.</summary>
    [JsonPropertyName("username")] public string? Username { get; set; }

    [JsonPropertyName("password")] public string? Password { get; set; }

    /// <summary>An API key is an alternative to a login, and is preferable if you have one.</summary>
    [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
}

/// <summary>
/// Minimal read-only Seq client: sign in if credentials were given, then query events. Seq issues
/// a session cookie plus a CSRF token, and both have to come back on every request.
/// </summary>
public sealed class SeqClient(SeqOptions options) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { CookieContainer = new CookieContainer() })
    {
        BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private bool _signedIn;
    private string? _csrfToken;

    public string Url => options.Url;

    public async Task<IReadOnlyList<SeqEvent>> QueryAsync(string filter, int count, CancellationToken cancellationToken)
    {
        await EnsureSignedInAsync(cancellationToken);

        var path = $"api/events?filter={Uri.EscapeDataString(filter)}&count={count}&render=true";
        var response = await SendAsync(path, cancellationToken);

        // The session can expire, or Seq can restart; one silent re-login covers it.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            _signedIn = false;
            await EnsureSignedInAsync(cancellationToken);
            response = await SendAsync(path, cancellationToken);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // A rejected query comes back 200 with an object rather than the expected array.
            if (!response.IsSuccessStatusCode || !body.TrimStart().StartsWith('['))
            {
                throw new SeqException(Describe(body, response.StatusCode));
            }

            return JsonSerializer.Deserialize<List<SeqEvent>>(body, JsonOptions) ?? [];
        }
    }

    private Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Seq-CSRF", _csrfToken ?? "none");

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Add("X-Seq-ApiKey", options.ApiKey);
        }

        return _http.SendAsync(request, cancellationToken);
    }

    private async Task EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        if (_signedIn || string.IsNullOrWhiteSpace(options.Username))
        {
            _signedIn = true;
            return;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "api/users/login")
        {
            Content = JsonContent.Create(new { options.Username, options.Password }),
        };

        // The header must be present on the login post; its value is only checked against the
        // cookie on later requests.
        request.Headers.Add("X-Seq-CSRF", "login");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SeqException($"Seq rejected the login for '{options.Username}' ({(int)response.StatusCode}).");
        }

        _csrfToken = JsonSerializer.Deserialize<SeqLogin>(body, JsonOptions)?.CsrfToken
            ?? throw new SeqException("Seq accepted the login but returned no CSRF token.");
        _signedIn = true;
    }

    private static string Describe(string body, HttpStatusCode statusCode)
    {
        try
        {
            var error = JsonSerializer.Deserialize<SeqError>(body, JsonOptions)?.Error;
            if (!string.IsNullOrWhiteSpace(error))
            {
                return $"Seq: {error}";
            }
        }
        catch (JsonException)
        {
        }

        return $"Seq returned {(int)statusCode}.";
    }

    public void Dispose() => _http.Dispose();

    private sealed record SeqLogin(string? CsrfToken);

    private sealed record SeqError(string? Error);
}

public sealed class SeqException(string message) : Exception(message);

public sealed class SeqEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string? Level { get; set; }
    public string? RenderedMessage { get; set; }
    public string? Exception { get; set; }
    public List<SeqProperty> Properties { get; set; } = [];

    /// <summary>One console-ish line, plus any exception indented beneath it.</summary>
    public IEnumerable<string> Render()
    {
        yield return $"{Timestamp.ToLocalTime():HH:mm:ss.fff}  {Abbreviate(Level),-3}  {RenderedMessage}";

        if (string.IsNullOrWhiteSpace(Exception))
        {
            yield break;
        }

        foreach (var line in Exception.ReplaceLineEndings("\n").Split('\n'))
        {
            yield return $"                 {line}";
        }
    }

    private static string Abbreviate(string? level) => level switch
    {
        "Verbose" => "VRB",
        "Debug" => "DBG",
        "Information" => "INF",
        "Warning" => "WRN",
        "Error" => "ERR",
        "Fatal" => "FTL",
        null => "",
        _ => level.Length <= 3 ? level.ToUpperInvariant() : level[..3].ToUpperInvariant(),
    };
}

public sealed class SeqProperty
{
    public string Name { get; set; } = "";
    public object? Value { get; set; }
}
