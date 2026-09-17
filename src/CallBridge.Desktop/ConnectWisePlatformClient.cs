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
        if (request.RequestUri is null) throw new ArgumentException("A ConnectWise Platform request URI is required.", nameof(request));
        var target = request.RequestUri.IsAbsoluteUri ? request.RequestUri : new Uri(new Uri(_baseUrl + "/"), request.RequestUri);
        if (!string.Equals(target.GetLeftPart(UriPartial.Authority), _baseUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ConnectWise Platform credentials cannot be sent to a different origin.");
        request.RequestUri = target;

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

    // ---- Ticketing (ConnectWise Platform Partner API: /api/platform/v2/service/ticketing, /api/platform/v1/company) ----

    public const string TicketingScopes = "platform.tickets.read platform.tickets.create platform.tickets.update platform.companies.read";
    private const string TicketsPath = "/api/platform/v2/service/ticketing/tickets";
    private static readonly ConcurrentDictionary<string, (DateTimeOffset LoadedAt, List<PlatformCompany> Companies)> CompanyCache = new();

    /// <summary>Company as returned by the platform, with the phone numbers usable for caller matching.</summary>
    public sealed record PlatformCompany(string Id, string Name, string ContactId, string ContactName, string[] Phones, string[] ExternalIds);

    public sealed record PlatformLookup(string Id, string Name);

    public static bool IsPlatformId(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;

    public async Task<List<PlatformCompany>> GetCompaniesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && CompanyCache.TryGetValue(_cacheKey, out var cached) && cached.LoadedAt > DateTimeOffset.UtcNow.AddMinutes(-15))
            return cached.Companies;
        using var document = await GetJsonAsync("/api/platform/v1/company/companies", cancellationToken);
        var companies = new List<PlatformCompany>();
        foreach (var company in Items(document.RootElement))
        {
            var id = StringProperty(company, "id");
            var name = FirstNonEmpty(StringProperty(company, "friendlyName"), StringProperty(company, "name"));
            if (!IsPlatformId(id) || string.IsNullOrWhiteSpace(name)) continue;
            if (company.TryGetProperty("inactiveDate", out var inactive) && inactive.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(inactive.GetString(), out var inactiveAt) && inactiveAt <= DateTimeOffset.UtcNow) continue;
            var phones = new List<string>();
            var contactId = "";
            var contactName = "";
            if (company.TryGetProperty("primaryContact", out var contact) && contact.ValueKind == JsonValueKind.Object)
            {
                contactId = StringProperty(contact, "id");
                contactName = $"{StringProperty(contact, "firstName")} {StringProperty(contact, "lastName")}".Trim();
                AddPhone(phones, contact, "primaryPhoneNumber");
            }
            if (company.TryGetProperty("primarySite", out var site) && site.ValueKind == JsonValueKind.Object)
                AddPhone(phones, site, "primaryPhoneNumber");
            var externalIds = company.TryGetProperty("externalIds", out var ext) && ext.ValueKind == JsonValueKind.Array
                ? ext.EnumerateArray().Select(item => StringProperty(item, "externalId")).Where(value => value.Length > 0).ToArray()
                : [];
            companies.Add(new(id, name, contactId, contactName, phones.Distinct(StringComparer.Ordinal).ToArray(), externalIds));
        }
        CompanyCache[_cacheKey] = (DateTimeOffset.UtcNow, companies);
        return companies;
    }

    /// <summary>Resolves a company reference that may be a platform UUID or a PSA numeric company ID (via external ID mapping or exact name).</summary>
    public async Task<PlatformCompany?> ResolveCompanyAsync(string companyId, string? companyName, CancellationToken cancellationToken = default)
    {
        var companies = await GetCompaniesAsync(false, cancellationToken);
        if (IsPlatformId(companyId)) return companies.FirstOrDefault(c => string.Equals(c.Id, companyId, StringComparison.OrdinalIgnoreCase));
        var byExternal = companies.Where(c => c.ExternalIds.Contains(companyId, StringComparer.Ordinal)).ToList();
        if (byExternal.Count == 1) return byExternal[0];
        if (string.IsNullOrWhiteSpace(companyName)) return null;
        var byName = companies.Where(c => string.Equals(c.Name, companyName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public async Task<List<PlatformLookup>> GetServiceBoardsAsync(CancellationToken cancellationToken = default) => await GetLookupsAsync("/api/platform/v1/service/ticketing/service-boards", cancellationToken);
    public async Task<List<PlatformLookup>> GetSourcesAsync(CancellationToken cancellationToken = default) => await GetLookupsAsync("/api/platform/v1/service/ticketing/sources", cancellationToken);

    public async Task<List<ConnectWiseTicketSummary>> GetOpenTicketsAsync(string platformCompanyId, int maximum = 5, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformId(platformCompanyId)) throw new ArgumentException("ConnectWise Platform company ID must be a UUID.", nameof(platformCompanyId));
        var closed = new List<string>();
        using (var statuses = await GetJsonAsync("/api/platform/v1/service/ticketing/statuses", cancellationToken))
            foreach (var status in Items(statuses.RootElement))
                if (string.Equals(StringProperty(status, "category"), "Closed", StringComparison.OrdinalIgnoreCase) && IsPlatformId(StringProperty(status, "id")))
                    closed.Add(StringProperty(status, "id"));
        var query = $"companyIds={Uri.EscapeDataString(platformCompanyId)}&pageSize={Math.Clamp(maximum, 1, 25)}&pageNum=1&sortBy=createdAt&sortDir=desc";
        if (closed.Count > 0) query += "&statusIds=" + Uri.EscapeDataString("[notIn]," + string.Join(',', closed));
        using var document = await GetJsonAsync($"{TicketsPath}?{query}", cancellationToken);
        var root = document.RootElement;
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tickets", out var ticketsNode) ? ticketsNode : root;
        var tickets = new List<ConnectWiseTicketSummary>();
        foreach (var ticket in Items(list))
        {
            var id = StringProperty(ticket, "id");
            if (!IsPlatformId(id)) continue;
            var statusCategory = ticket.TryGetProperty("status", out var s) ? StringProperty(s, "name") : "";
            tickets.Add(new(id, StringProperty(ticket, "summary"),
                ticket.TryGetProperty("priority", out var p) ? StringProperty(p, "name") : "",
                statusCategory,
                NullIfEmpty(StringProperty(ticket, "number"))));
        }
        return tickets;
    }

    public async Task<ConnectWiseTicketSummary> CreateTicketAsync(string platformCompanyId, string serviceBoardId, string sourceId, string summary, string description, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformId(platformCompanyId)) throw new ArgumentException("Choose a ConnectWise company for the ticket.", nameof(platformCompanyId));
        if (!IsPlatformId(serviceBoardId) || !IsPlatformId(sourceId))
            throw new ArgumentException("Ask your admin to choose a default service board and source in Settings > ConnectWise.");
        var payload = new
        {
            summary = Clip(summary, 255),
            description = Clip(string.IsNullOrWhiteSpace(description) ? summary : description, 10000),
            serviceBoard = new { id = serviceBoardId },
            source = new { id = sourceId },
            company = new { id = platformCompanyId }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, TicketsPath) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        using var response = await SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ApiError(response, content));
        using var document = JsonDocument.Parse(content);
        var id = StringProperty(document.RootElement, "id");
        if (!IsPlatformId(id)) throw new InvalidDataException("ConnectWise Platform didn't return the new ticket's ID.");
        return new(id, payload.summary, "", "New", NullIfEmpty(StringProperty(document.RootElement, "number")));
    }

    /// <summary>Adds a note visible only to the partner (internal).</summary>
    public async Task AddTicketNoteAsync(string ticketId, string text, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformId(ticketId)) throw new ArgumentException("ConnectWise Platform ticket ID must be a UUID.", nameof(ticketId));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Note text is required.", nameof(text));
        var payload = new { detail = Clip(text.Trim(), 12000), visibility = 2 };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/v1/service/ticketing/tickets/{Uri.EscapeDataString(ticketId)}/notes")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ApiError(response, await response.Content.ReadAsStringAsync(cancellationToken)));
    }

    /// <summary>The platform API has no time entries, so confirmed call time is recorded as an internal note.</summary>
    public static string TimeNoteText(string memberIdentifier, DateTimeOffset start, DateTimeOffset end, string notes)
    {
        var minutes = Math.Max(1, (int)Math.Round((end - start).TotalMinutes));
        var who = string.IsNullOrWhiteSpace(memberIdentifier) ? "" : $" by {memberIdentifier.Trim()}";
        var body = string.IsNullOrWhiteSpace(notes) ? "" : $"\n{notes.Trim()}";
        return $"Phone call time{who}: {minutes} min ({start.ToLocalTime():MMM d, h:mm tt} – {end.ToLocalTime():h:mm tt}).{body}";
    }

    private async Task<List<PlatformLookup>> GetLookupsAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(path, cancellationToken);
        return Items(document.RootElement)
            .Where(item => !(item.TryGetProperty("inactiveFlag", out var inactive) && inactive.ValueKind == JsonValueKind.True))
            .Select(item => new PlatformLookup(StringProperty(item, "id"), StringProperty(item, "name")))
            .Where(item => IsPlatformId(item.Id) && item.Name.Length > 0)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ApiError(response, content));
        return JsonDocument.Parse(content);
    }

    /// <summary>Returns the items of an array response, or of the first array property when the response wraps it.</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Array) return property.Value.EnumerateArray().ToList();
        return [];
    }

    private static void AddPhone(List<string> phones, JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var phone) || phone.ValueKind != JsonValueKind.Object) return;
        if (phone.TryGetProperty("activeFlag", out var active) && active.ValueKind == JsonValueKind.False) return;
        var national = StringProperty(phone, "nationalNumber");
        if (string.IsNullOrWhiteSpace(national)) return;
        var country = StringProperty(phone, "countryCode").TrimStart('+');
        phones.Add(string.IsNullOrWhiteSpace(country) ? national : $"+{country}{national}");
    }

    private static string ApiError(HttpResponseMessage response, string content) =>
        $"ConnectWise Platform returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {SafeDetail(content)}".Trim();
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Clip(string value, int max) { var text = (value ?? "").Trim(); return text.Length <= max ? text : text[..max]; }
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
