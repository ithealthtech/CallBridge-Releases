using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CallBridge.Desktop;

public sealed record ConnectWisePlatformResult(bool Success, string Message, DateTimeOffset? ExpiresAt = null);

public sealed class ConnectWisePlatformClient : IDisposable
{
    public const string NorthAmericaBaseUrl = "https://openapi.service.itsupport247.net";
    public const string EuropeBaseUrl = "https://openapi.service.euplatform.connectwise.com";
    public const string AustraliaBaseUrl = "https://openapi.service.auplatform.connectwise.com";

    private sealed record TokenEntry(string AccessToken, DateTimeOffset ExpiresAt);
    private sealed record RateLimitEntry(int? Limit, int? Remaining, DateTimeOffset? ResetAt);
    private static readonly ConcurrentDictionary<string, TokenEntry> TokenCache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TokenLocks = new();
    private static readonly ConcurrentDictionary<string, RateLimitEntry> RateLimits = new();

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;
    private readonly string _cacheKey;
    private readonly AppSettings _settings;

    public ConnectWisePlatformClient(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _baseUrl = NormalizeBaseUrl(settings.ConnectWisePlatformBaseUrl);
        _clientId = settings.ConnectWisePlatformClientId.Trim();
        _clientSecret = settings.ConnectWisePlatformClientSecret;
        _scope = NormalizeScopes(settings.ConnectWisePlatformScopes);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _cacheKey = $"{_baseUrl}|{_clientId}|{_scope}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_clientSecret)))}";
        if (!string.IsNullOrWhiteSpace(settings.ConnectWisePlatformAccessToken)
            && settings.ConnectWisePlatformAccessTokenExpiresAt is { } storedExpiry
            && storedExpiry > DateTimeOffset.UtcNow.AddMinutes(1))
            TokenCache.TryAdd(_cacheKey, new(settings.ConnectWisePlatformAccessToken, storedExpiry));
    }

    public static bool IsConfigured(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ConnectWisePlatformBaseUrl) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWisePlatformClientId) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWisePlatformClientSecret) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWisePlatformScopes);

    public async Task<ConnectWisePlatformResult> TestAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret) || string.IsNullOrWhiteSpace(_scope))
            return new(false, "ConnectWise Platform Client ID, Client Secret, and scopes are required.");

        try
        {
            var token = await GetTokenAsync(cancellationToken);
            return new(true, $"ConnectWise Platform OAuth succeeded. Token cached until {token.ExpiresAt.LocalDateTime:g}.", token.ExpiresAt);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    public async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(HttpMethod method, string relativePath, CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(method, new Uri(new Uri(_baseUrl + "/"), relativePath.TrimStart('/')));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (RateLimits.TryGetValue(_cacheKey, out var known)
            && known.Remaining == 0
            && known.ResetAt is { } resetAt
            && resetAt > DateTimeOffset.UtcNow)
            throw new HttpRequestException($"ConnectWise Platform quota is exhausted. Calls are paused until {resetAt.LocalDateTime:g}.");

        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        var response = await _http.SendAsync(request, cancellationToken);
        var limit = HeaderNumber(response, "Limit", "X-RateLimit-Limit");
        var remaining = HeaderNumber(response, "Remaining", "X-RateLimit-Remaining");
        var reset = HeaderReset(response, "Reset", "X-RateLimit-Reset");
        RateLimits[_cacheKey] = new(limit, remaining, reset);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAt = reset ?? RetryAfter(response) ?? DateTimeOffset.UtcNow.AddMinutes(5);
            RateLimits[_cacheKey] = new(limit ?? 500, 0, retryAt);
            var detail = SafeDetail(await response.Content.ReadAsStringAsync(cancellationToken));
            response.Dispose();
            throw new HttpRequestException($"ConnectWise Platform returned 429 Too Many Requests. Calls are paused until {retryAt.LocalDateTime:g}. {detail}".Trim());
        }
        return response;
    }

    private async Task<TokenEntry> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TokenCache.TryGetValue(_cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;

        var gate = TokenLocks.GetOrAdd(_cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TokenCache.TryGetValue(_cacheKey, out cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;

            var payload = JsonSerializer.Serialize(new
            {
                grant_type = "client_credentials",
                client_id = _clientId,
                client_secret = _clientSecret,
                scope = _scope
            });
            using var response = await _http.PostAsync($"{_baseUrl}/v1/token", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = SafeDetail(content);
                if (response.StatusCode == HttpStatusCode.Locked)
                    throw new HttpRequestException($"ConnectWise Platform returned 423 Locked. Reuse the unexpired token or wait for it to expire. {detail}".Trim());
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAt = HeaderReset(response, "Reset", "X-RateLimit-Reset") ?? RetryAfter(response) ?? DateTimeOffset.UtcNow.AddMinutes(5);
                    throw new HttpRequestException($"ConnectWise Platform returned 429 Too Many Requests. Try again after {retryAt.LocalDateTime:g}. {detail}".Trim());
                }
                throw new HttpRequestException($"ConnectWise Platform returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim());
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var accessToken = StringProperty(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken)) accessToken = StringProperty(root, "Access_Token");
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidDataException("ConnectWise Platform did not return an access token.");
            var expiresIn = NumberProperty(root, "expires_in", 3600);
            var entry = new TokenEntry(accessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(120, expiresIn)));
            TokenCache[_cacheKey] = entry;
            _settings.ConnectWisePlatformAccessToken = entry.AccessToken;
            _settings.ConnectWisePlatformAccessTokenExpiresAt = entry.ExpiresAt;
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string NormalizeBaseUrl(string value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? NorthAmericaBaseUrl : value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("ConnectWise Platform API URL must be a valid HTTPS URL.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string NormalizeScopes(string value) => string.Join(' ', value.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal));
    private static string StringProperty(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int NumberProperty(JsonElement element, string name, int fallback) => element.TryGetProperty(name, out var value) && (value.TryGetInt32(out var parsed) || int.TryParse(value.ToString(), out parsed)) ? parsed : fallback;
    private static int? HeaderNumber(HttpResponseMessage response, params string[] names)
    {
        foreach (var name in names)
            if (response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var number)) return number;
        return null;
    }
    private static DateTimeOffset? HeaderReset(HttpResponseMessage response, params string[] names)
    {
        foreach (var name in names)
            if (response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var epoch))
                try { return DateTimeOffset.FromUnixTimeSeconds(epoch); } catch (ArgumentOutOfRangeException) { return null; }
        return null;
    }
    private static DateTimeOffset? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date) return date;
        if (retry?.Delta is { } delta) return DateTimeOffset.UtcNow.Add(delta);
        return null;
    }
    private static string SafeDetail(string value)
    {
        var detail = value.Trim();
        return detail.Length <= 500 ? detail : detail[..500];
    }

    public void Dispose() => _http.Dispose();
}
