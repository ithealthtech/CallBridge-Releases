using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CallBridge.Desktop;

public sealed record ConnectWiseResult(bool Success, string Message);
public sealed record ConnectWiseContactRecord(string CompanyId, string CompanyName, string ContactId, string ContactName, string[] Phones);
public sealed record ConnectWiseTicketSummary(string Id, string Summary, string Priority, string Status, string? Number = null)
{
    /// <summary>Human-readable ticket number: the PSA ID, or the platform ticket number (platform IDs are UUIDs).</summary>
    public string DisplayNumber => Number ?? Id;
}
/// <summary>A status or priority option. Closed marks statuses that close the ticket.</summary>
public sealed record TicketChoice(string Id, string Name, bool Closed)
{
    public override string ToString() => Name;
}

/// <summary>A ticket's current status, priority, and owner, plus the choices for changing them.</summary>
public sealed record TicketEditState(string StatusId, string PriorityId, string Owner, IReadOnlyList<TicketChoice> Statuses, IReadOnlyList<TicketChoice> Priorities, bool CanAssign);

public sealed record ConnectWisePhoneType(int Id, string Name)
{
    public override string ToString() => Name;
}
/// <summary>A phone number already on a contact. PSA allows one number per communication type on a contact.</summary>
public sealed record ContactPhoneItem(string ItemId, int TypeId, string TypeName, string Value);

public sealed record ConnectWiseContactSummary(string Id, string Name, string[] Phones)
{
    public IReadOnlyList<ContactPhoneItem> PhoneItems { get; init; } = [];
    public override string ToString() => Phones.Length == 0 ? Name : $"{Name} ({string.Join(", ", Phones)})";
}
public sealed record ConnectWiseCompanySummary(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class ConnectWiseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _siteBase;

    public ConnectWiseClient(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _siteBase = NormalizeSite(settings.ConnectWiseSite);
        _apiBase = $"{_siteBase}/v4_6_release/apis/3.0";
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Per-request limits below: reads stay quick, but PSA runs board workflows and notifications while creating a ticket.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        var identity = $"{settings.ConnectWiseCompanyId}+{settings.ConnectWisePublicKey}:{settings.ConnectWisePrivateKey}";
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(identity)));
        _http.DefaultRequestHeaders.Add("clientId", settings.ConnectWiseClientId);
        _http.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.connectwise.com+json; version=2022.1"));
    }

    public static bool IsConfigured(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ConnectWiseSite) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWiseCompanyId) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWisePublicKey) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWisePrivateKey) &&
        !string.IsNullOrWhiteSpace(settings.ConnectWiseClientId);

    public async Task<ConnectWiseResult> TestAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReadTimeout);
            using var response = await _http.GetAsync($"{_apiBase}/company/companies?pageSize=1&fields=id,name", timeout.Token);
            if (response.IsSuccessStatusCode) return new(true, "ConnectWise PSA authentication succeeded.");
            return new(false, await ErrorAsync(response));
        }
        catch (Exception ex) { return new(false, $"ConnectWise connection failed: {ex.Message}"); }
    }

    public async Task<List<ConnectWiseContactRecord>> DownloadContactsAsync(IProgress<string>? progress = null)
    {
        var contacts = new List<ConnectWiseContactRecord>();
        const int pageSize = 1000;
        for (var page = 1; page <= 100; page++)
        {
            progress?.Report($"Downloading ConnectWise contacts, page {page}...");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var response = await _http.GetAsync($"{_apiBase}/company/contacts?conditions=inactiveFlag=false&pageSize={pageSize}&page={page}", timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ConnectWise returned an unexpected contacts response.");
            var count = 0;
            foreach (var contact in document.RootElement.EnumerateArray())
            {
                count++;
                var contactId = Property(contact, "id");
                var first = Property(contact, "firstName");
                var last = Property(contact, "lastName");
                var contactName = $"{first} {last}".Trim();
                if (string.IsNullOrWhiteSpace(contactName)) contactName = Property(contact, "identifier");
                var company = contact.TryGetProperty("company", out var companyNode) ? companyNode : default;
                var companyId = company.ValueKind == JsonValueKind.Object ? Property(company, "id") : "";
                var companyName = company.ValueKind == JsonValueKind.Object ? Property(company, "name") : "";
                if (string.IsNullOrWhiteSpace(companyName) && company.ValueKind == JsonValueKind.Object) companyName = Property(company, "identifier");
                var phones = ExtractPhones(contact).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!string.IsNullOrWhiteSpace(contactId) && !string.IsNullOrWhiteSpace(companyId) && !string.IsNullOrWhiteSpace(companyName))
                    contacts.Add(new(companyId, companyName, contactId, contactName, phones));
            }
            if (count < pageSize) break;
        }
        return contacts;
    }

    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    public static TimeSpan CreateTicketTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public async Task<JsonElement> CreateTicketAsync(string companyId, int boardId, string summary, string description)
    {
        if (!long.TryParse(companyId, out var numericCompanyId) || numericCompanyId <= 0)
            throw new ArgumentException("Choose a ConnectWise company for the ticket.", nameof(companyId));
        if (boardId <= 0) throw new ArgumentException("A default service board ID is required.", nameof(boardId));
        var payload = new { summary, company = new { id = numericCompanyId }, board = new { id = boardId }, initialDescription = description };
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = new CancellationTokenSource(CreateTicketTimeout);
            using var response = await _http.PostAsync($"{_apiBase}/service/tickets", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException { StatusCode: null })
        {
            // A slow or dropped response doesn't mean PSA didn't create the ticket. Look for it before reporting
            // a failure, so a retry doesn't create a duplicate.
            if (await FindRecentTicketAsync(numericCompanyId, boardId, summary, startedAt) is { } existing) return existing;
            throw new TimeoutException("ConnectWise didn't confirm the ticket in time, and it wasn't found afterwards. Check ConnectWise before trying again.", ex);
        }
    }

    internal async Task<JsonElement?> FindRecentTicketAsync(long companyId, int boardId, string summary, DateTimeOffset since)
    {
        var escaped = summary.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var conditions = Uri.EscapeDataString($"company/id={companyId} and board/id={boardId} and summary=\"{escaped}\" and dateEntered>=[{since.AddMinutes(-2).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}]");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(ReadTimeout);
                using var response = await _http.GetAsync($"{_apiBase}/service/tickets?conditions={conditions}&orderBy=id%20desc&pageSize=1&fields=id,summary", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
                        return document.RootElement[0].Clone();
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException) { }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return null;
    }

    public async Task<List<ConnectWiseTicketSummary>> GetOpenTicketsAsync(string companyId, int maximum = 5, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(companyId, out var numericCompanyId) || numericCompanyId <= 0)
            throw new ArgumentException("ConnectWise company ID must be numeric.", nameof(companyId));
        var pageSize = Math.Clamp(maximum, 1, 25);
        var conditions = Uri.EscapeDataString($"company/id={numericCompanyId} and closedFlag=false");
        var url = $"{_apiBase}/service/tickets?conditions={conditions}&orderBy=id%20desc&pageSize={pageSize}&fields=id,summary,priority/name,status/name";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        using var response = await _http.GetAsync(url, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ConnectWise returned an unexpected tickets response.");
        var tickets = new List<ConnectWiseTicketSummary>();
        foreach (var ticket in document.RootElement.EnumerateArray())
        {
            var id = Property(ticket, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var priority = ticket.TryGetProperty("priority", out var priorityNode) ? Property(priorityNode, "name") : "";
            var status = ticket.TryGetProperty("status", out var statusNode) ? Property(statusNode, "name") : "";
            tickets.Add(new(id, Property(ticket, "summary"), priority, status));
        }
        return tickets;
    }

    /// <summary>Finds active companies whose name starts with the query, for choosing a ticket's company.</summary>
    public async Task<List<ConnectWiseCompanySummary>> SearchCompaniesAsync(string query, int maximum = 10, CancellationToken cancellationToken = default)
    {
        var text = new string((query ?? "").Trim().Where(ch => ch is not ('"' or '\\' or '*' or '%')).ToArray());
        if (text.Length is 0 or > 100) throw new ArgumentException("Enter 1 to 100 characters to search companies.", nameof(query));
        var conditions = Uri.EscapeDataString($"name like \"{text}*\" and deletedFlag=false");
        var url = $"{_apiBase}/company/companies?conditions={conditions}&orderBy=name%20asc&pageSize={Math.Clamp(maximum, 1, 25)}&fields=id,name";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        using var response = await _http.GetAsync(url, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ConnectWise returned an unexpected companies response.");
        return document.RootElement.EnumerateArray()
            .Select(company => new ConnectWiseCompanySummary(Property(company, "id"), Property(company, "name")))
            .Where(company => long.TryParse(company.Id, out _) && !string.IsNullOrWhiteSpace(company.Name))
            .ToList();
    }

    public static bool IsCompanyId(string? companyId) => (long.TryParse(companyId, out var id) && id > 0) || ConnectWisePlatformClient.IsPlatformId(companyId);

    /// <summary>Adds an internal-analysis note to a service ticket.</summary>
    public async Task AddTicketNoteAsync(string ticketId, string text, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(ticketId, out var numericTicketId) || numericTicketId <= 0)
            throw new ArgumentException("ConnectWise ticket ID must be numeric.", nameof(ticketId));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Note text is required.", nameof(text));
        var payload = new { text = text.Trim(), detailDescriptionFlag = false, internalAnalysisFlag = true, resolutionFlag = false };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.PostAsync($"{_apiBase}/service/tickets/{numericTicketId}/notes", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
    }

    /// <summary>Logs time against a service ticket for the given member.</summary>
    public async Task CreateTimeEntryAsync(string ticketId, string memberIdentifier, DateTimeOffset start, DateTimeOffset end, string notes, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(ticketId, out var numericTicketId) || numericTicketId <= 0)
            throw new ArgumentException("ConnectWise ticket ID must be numeric.", nameof(ticketId));
        var member = (memberIdentifier ?? "").Trim();
        if (member.Length is 0 or > 50 || member.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
            throw new ArgumentException("ConnectWise member ID must be letters, numbers, dots, dashes, or underscores.", nameof(memberIdentifier));
        if (end <= start) throw new ArgumentException("Time entry end must be after its start.", nameof(end));
        var payload = new
        {
            chargeToId = numericTicketId,
            chargeToType = "ServiceTicket",
            member = new { identifier = member },
            timeStart = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            timeEnd = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            notes = string.IsNullOrWhiteSpace(notes) ? "Phone call" : notes.Trim()
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.PostAsync($"{_apiBase}/time/entries", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
    }

    /// <summary>
    /// Finds who a phone number belongs to directly in PSA: first a contact with that number, then a company whose
    /// main number it is. Used when the number isn't in CallBridge's local directory yet.
    /// </summary>
    public async Task<ConnectWiseContactRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        if (digits.Length < 7) return null;
        if (digits.Length > 10) digits = digits[^10..];

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ReadTimeout);
            var conditions = Uri.EscapeDataString("inactiveFlag=false");
            var child = Uri.EscapeDataString($"communicationItems/value like \"%{digits}%\"");
            using var response = await _http.GetAsync($"{_apiBase}/company/contacts?conditions={conditions}&childConditions={child}&pageSize=5&fields=id,firstName,lastName,company,communicationItems", timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var contact in document.RootElement.EnumerateArray())
                {
                    var company = contact.TryGetProperty("company", out var node) ? node : default;
                    var companyId = company.ValueKind == JsonValueKind.Object ? Property(company, "id") : "";
                    var companyName = company.ValueKind == JsonValueKind.Object ? FirstNonEmpty(Property(company, "name"), Property(company, "identifier")) : "";
                    var name = $"{Property(contact, "firstName")} {Property(contact, "lastName")}".Trim();
                    if (long.TryParse(companyId, out _) && companyName.Length > 0 && name.Length > 0)
                        return new(companyId, companyName, Property(contact, "id"), name, ExtractPhones(contact).ToArray());
                }
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ReadTimeout);
            var conditions = Uri.EscapeDataString($"phoneNumber like \"%{digits}%\" and deletedFlag=false");
            using var response = await _http.GetAsync($"{_apiBase}/company/companies?conditions={conditions}&pageSize=2&fields=id,name,phoneNumber", timeout.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            // A main number shared by more than one company can't identify the caller.
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() == 1)
            {
                var company = document.RootElement[0];
                var id = Property(company, "id");
                var name = Property(company, "name");
                if (long.TryParse(id, out _) && name.Length > 0) return new(id, name, "", "", [Property(company, "phoneNumber")]);
            }
        }
        return null;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    /// <summary>The ticket fields the call panel edits: board (for its statuses), status, priority, and owner.</summary>
    public async Task<TicketEditState> GetTicketEditStateAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        var id = NumericTicketId(ticketId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        using var response = await _http.GetAsync($"{_apiBase}/service/tickets/{id}?fields=id,board/id,status/id,status/name,priority/id,priority/name,owner/identifier", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = document.RootElement;
        string Nested(string parent, string child) => root.TryGetProperty(parent, out var node) && node.ValueKind == JsonValueKind.Object ? Property(node, child) : "";
        var boardId = Nested("board", "id");
        var statuses = await GetBoardStatusesAsync(boardId, timeout.Token);
        var priorities = await GetPrioritiesAsync(timeout.Token);
        return new TicketEditState(Nested("status", "id"), Nested("priority", "id"), Nested("owner", "identifier"), statuses, priorities, CanAssign: true);
    }

    private async Task<List<TicketChoice>> GetBoardStatusesAsync(string boardId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(boardId, out var board) || board <= 0) return [];
        var conditions = Uri.EscapeDataString("inactive=false");
        using var response = await _http.GetAsync($"{_apiBase}/service/boards/{board}/statuses?conditions={conditions}&orderBy=sortOrder%20asc&pageSize=100&fields=id,name,closedStatus", cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.ValueKind != JsonValueKind.Array ? [] : document.RootElement.EnumerateArray()
            .Select(status => new TicketChoice(Property(status, "id"), Property(status, "name"),
                status.TryGetProperty("closedStatus", out var closed) && closed.ValueKind == JsonValueKind.True))
            .Where(status => long.TryParse(status.Id, out _) && status.Name.Length > 0)
            .ToList();
    }

    private async Task<List<TicketChoice>> GetPrioritiesAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"{_apiBase}/service/priorities?orderBy=sortOrder%20asc&pageSize=50&fields=id,name", cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.ValueKind != JsonValueKind.Array ? [] : document.RootElement.EnumerateArray()
            .Select(priority => new TicketChoice(Property(priority, "id"), Property(priority, "name"), false))
            .Where(priority => long.TryParse(priority.Id, out _) && priority.Name.Length > 0)
            .ToList();
    }

    /// <summary>Changes the ticket's status, priority, and/or owner in one PATCH. Null values are left alone.</summary>
    public async Task UpdateTicketAsync(string ticketId, string? statusId, string? priorityId, string? ownerIdentifier, CancellationToken cancellationToken = default)
    {
        var id = NumericTicketId(ticketId);
        var operations = new List<object>();
        if (statusId is not null) operations.Add(new { op = "replace", path = "status", value = new { id = long.Parse(NumericChoice(statusId, "status")) } });
        if (priorityId is not null) operations.Add(new { op = "replace", path = "priority", value = new { id = long.Parse(NumericChoice(priorityId, "priority")) } });
        if (ownerIdentifier is not null)
        {
            var member = ownerIdentifier.Trim();
            if (member.Length is 0 or > 50 || member.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
                throw new ArgumentException("Add your ConnectWise member ID in Settings > Sign-in first.", nameof(ownerIdentifier));
            operations.Add(new { op = "replace", path = "owner", value = new { identifier = member } });
        }
        if (operations.Count == 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{_apiBase}/service/tickets/{id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(operations), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
    }

    /// <summary>Adds a resolution note (the note type PSA shows as the ticket's resolution).</summary>
    public async Task AddResolutionNoteAsync(string ticketId, string text, CancellationToken cancellationToken = default)
    {
        var id = NumericTicketId(ticketId);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a resolution.", nameof(text));
        var payload = new { text = text.Trim(), detailDescriptionFlag = false, internalAnalysisFlag = false, resolutionFlag = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.PostAsync($"{_apiBase}/service/tickets/{id}/notes", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
    }

    private static long NumericTicketId(string ticketId) =>
        long.TryParse(ticketId, out var id) && id > 0 ? id : throw new ArgumentException("ConnectWise ticket ID must be numeric.", nameof(ticketId));

    private static string NumericChoice(string value, string what) =>
        long.TryParse(value, out var id) && id > 0 ? value : throw new ArgumentException($"Choose a valid ticket {what}.", what);

    /// <summary>Phone communication types (Direct, Mobile, and so on) configured in this PSA.</summary>
    public async Task<List<ConnectWisePhoneType>> GetPhoneTypesAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var conditions = Uri.EscapeDataString("phoneFlag=true");
        using var response = await _http.GetAsync($"{_apiBase}/company/communicationTypes?conditions={conditions}&pageSize=50&fields=id,description", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ConnectWise returned an unexpected phone types response.");
        return document.RootElement.EnumerateArray()
            .Select(type => (Id: int.TryParse(Property(type, "id"), out var id) ? id : 0, Name: Property(type, "description")))
            .Where(type => type.Id > 0 && type.Name.Length > 0)
            .Select(type => new ConnectWisePhoneType(type.Id, type.Name))
            .ToList();
    }

    /// <summary>Active contacts at a company, for choosing who a new phone number belongs to.</summary>
    public async Task<List<ConnectWiseContactSummary>> GetCompanyContactsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(companyId, out var numericCompanyId) || numericCompanyId <= 0)
            throw new ArgumentException("ConnectWise company ID must be numeric.", nameof(companyId));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var conditions = Uri.EscapeDataString($"company/id={numericCompanyId} and inactiveFlag=false");
        using var response = await _http.GetAsync($"{_apiBase}/company/contacts?conditions={conditions}&orderBy=firstName%20asc&pageSize=200&fields=id,firstName,lastName,communicationItems", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ConnectWise returned an unexpected contacts response.");
        return document.RootElement.EnumerateArray()
            .Select(contact => new ConnectWiseContactSummary(
                Property(contact, "id"),
                $"{Property(contact, "firstName")} {Property(contact, "lastName")}".Trim(),
                ExtractPhones(contact).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            { PhoneItems = ExtractPhoneItems(contact) })
            .Where(contact => long.TryParse(contact.Id, out _) && contact.Name.Length > 0)
            .ToList();
    }

    /// <summary>Every communication item on a contact with its type, so a new number can avoid a type that's taken.</summary>
    private static List<ContactPhoneItem> ExtractPhoneItems(JsonElement contact)
    {
        var items = new List<ContactPhoneItem>();
        if (!contact.TryGetProperty("communicationItems", out var list) || list.ValueKind != JsonValueKind.Array) return items;
        foreach (var item in list.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.Object ? typeNode : default;
            var typeId = type.ValueKind == JsonValueKind.Object && int.TryParse(Property(type, "id"), out var parsed) ? parsed : 0;
            if (typeId > 0) items.Add(new ContactPhoneItem(Property(item, "id"), typeId, type.ValueKind == JsonValueKind.Object ? Property(type, "name") : "", Property(item, "value")));
        }
        return items;
    }

    /// <summary>Replaces the number stored under one of a contact's existing phone types.</summary>
    public async Task ReplaceContactPhoneAsync(string contactId, string itemId, string phone, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(contactId, out var numericContactId) || numericContactId <= 0)
            throw new ArgumentException("ConnectWise contact ID must be numeric.", nameof(contactId));
        if (!long.TryParse(itemId, out var numericItemId) || numericItemId <= 0)
            throw new ArgumentException("The contact's existing phone entry couldn't be identified.", nameof(itemId));
        var operations = new[] { new { op = "replace", path = "value", value = PsaPhoneValue(phone) } };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{_apiBase}/company/contacts/{numericContactId}/communications/{numericItemId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(operations), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
    }

    /// <summary>Adds a phone number to an existing contact.</summary>
    public async Task AddContactPhoneAsync(string contactId, int phoneTypeId, string phone, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(contactId, out var numericContactId) || numericContactId <= 0)
            throw new ArgumentException("ConnectWise contact ID must be numeric.", nameof(contactId));
        var value = PsaPhoneValue(phone);
        var payload = new { type = new { id = phoneTypeId }, value, communicationType = "Phone", defaultFlag = false };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.PostAsync($"{_apiBase}/company/contacts/{numericContactId}/communications", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
    }

    /// <summary>Creates a contact at a company with the given phone number; returns the new contact ID.</summary>
    public async Task<string> CreateContactAsync(string companyId, string firstName, string lastName, int phoneTypeId, string phone, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(companyId, out var numericCompanyId) || numericCompanyId <= 0)
            throw new ArgumentException("Choose a ConnectWise company for the contact.", nameof(companyId));
        var first = (firstName ?? "").Trim();
        if (first.Length is 0 or > 30) throw new ArgumentException("Enter a first name (up to 30 characters).", nameof(firstName));
        var last = (lastName ?? "").Trim();
        if (last.Length > 30) throw new ArgumentException("The last name can be up to 30 characters.", nameof(lastName));
        var payload = new
        {
            firstName = first,
            lastName = last,
            company = new { id = numericCompanyId },
            communicationItems = new[] { new { type = new { id = phoneTypeId }, value = PsaPhoneValue(phone), communicationType = "Phone", defaultFlag = true } }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.PostAsync($"{_apiBase}/company/contacts", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await ErrorAsync(response));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var id = Property(document.RootElement, "id");
        if (!long.TryParse(id, out _)) throw new InvalidDataException("ConnectWise didn't return the new contact's ID.");
        return id;
    }

    /// <summary>
    /// Phone digits as PSA stores them: North American numbers without the leading country code 1, others as
    /// dialed digits. Rejects extensions and anything that isn't a phone number.
    /// </summary>
    public static string PsaPhoneValue(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        if (digits.Length is < 7 or > 15) throw new ArgumentException("That doesn't look like a customer phone number.", nameof(phone));
        return digits;
    }

    public string CompanyUrl(string companyId) => $"{_siteBase}/v4_6_release/ConnectWise.aspx?locale=en_US&routeTo=Company.fv&recid={Uri.EscapeDataString(companyId)}";
    public string TicketUrl(string ticketId) => $"{_siteBase}/v4_6_release/ConnectWise.aspx?locale=en_US&routeTo=ServiceFV&recid={Uri.EscapeDataString(ticketId)}";

    private static IEnumerable<string> ExtractPhones(JsonElement contact)
    {
        if (!contact.TryGetProperty("communicationItems", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        {
            var type = $"{Property(item, "type")} {Property(item, "communicationType")}";
            if (!type.Contains("phone", StringComparison.OrdinalIgnoreCase) && !type.Contains("mobile", StringComparison.OrdinalIgnoreCase) && !type.Contains("cell", StringComparison.OrdinalIgnoreCase)) continue;
            if (type.Contains("fax", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Property(item, "value");
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }

    private static string Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static string NormalizeSite(string value)
    {
        var site = value.Trim().TrimEnd('/');
        if (!site.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !site.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) site = "https://" + site;
        if (!Uri.TryCreate(site, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("ConnectWise Site must be a valid HTTPS URL.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var detail = (await response.Content.ReadAsStringAsync()).Trim();
        if (detail.Length > 500) detail = detail[..500];
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Date;
            if (retry is null && response.Headers.RetryAfter?.Delta is { } delta) retry = DateTimeOffset.UtcNow.Add(delta);
            return retry is null
                ? $"ConnectWise returned HTTP 429 Too Many Requests. Pause requests until the quota resets. {detail}".Trim()
                : $"ConnectWise returned HTTP 429 Too Many Requests. Pause requests until {retry.Value.LocalDateTime:g}. {detail}".Trim();
        }
        return $"ConnectWise returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim();
    }
    public void Dispose() => _http.Dispose();
}
