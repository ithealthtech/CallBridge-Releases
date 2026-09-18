using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CallBridge.Desktop;

var testOnlyCredential = $"test-only-{Guid.Empty:N}";
var settings = new AppSettings
{
    ConnectWiseSite = "https://na.example.connectwise.net",
    ConnectWiseCompanyId = "acme",
    ConnectWisePublicKey = "public-key",
    ConnectWisePrivateKey = testOnlyCredential,
    ConnectWiseClientId = "client-id"
};

var handler = new FakeConnectWiseHandler();
using var client = new ConnectWiseClient(settings, handler);

var test = await client.TestAsync();
Assert(test.Success, test.Message);

var contacts = await client.DownloadContactsAsync();
Assert(contacts.Count == 1, "Expected one callable contact.");
var contact = contacts[0];
Assert(contact.CompanyId == "101" && contact.CompanyName == "Acme Widgets", "Company mapping failed.");
Assert(contact.ContactId == "202" && contact.ContactName == "Avery Stone", "Contact mapping failed.");
Assert(contact.Phones.SequenceEqual(new[] { "+1 (908) 555-0100", "908-555-0101" }), "Phone extraction or fax filtering failed.");

var ticket = await client.CreateTicketAsync("101", 7, "Phone support", "Caller needs assistance.");
Assert(ticket.GetProperty("id").GetInt32() == 9001, "Ticket response parsing failed.");
Assert(handler.SawValidAuthentication, "ConnectWise authentication headers were not sent.");
Assert(handler.SawExpectedTicket, "ConnectWise ticket request body was incorrect.");
var openTickets = await client.GetOpenTicketsAsync("101", 5);
Assert(handler.SawExpectedTicketQuery, "Open-ticket query conditions were incorrect.");
Assert(openTickets.Count == 1 && openTickets[0].Id == "48213" && openTickets[0].Priority == "Priority 2 - High" && openTickets[0].Status == "New", "Open-ticket mapping failed.");
try
{
    await client.GetOpenTicketsAsync("101 or 1=1");
    throw new InvalidOperationException("Non-numeric company IDs must be rejected before querying tickets.");
}
catch (ArgumentException)
{
}
await client.AddTicketNoteAsync("48213", "Printer back online after driver rollback.");
Assert(handler.SawExpectedTicketNote, "Ticket note request was incorrect.");
try
{
    await client.AddTicketNoteAsync("48213/../1", "x");
    throw new InvalidOperationException("Non-numeric ticket IDs must be rejected before adding notes.");
}
catch (ArgumentException)
{
}
var companies = await client.SearchCompaniesAsync("Blue \"Ridge");
Assert(handler.SawExpectedCompanySearch, "Company search conditions were incorrect or unescaped.");
Assert(companies.Count == 1 && companies[0].Id == "101" && companies[0].Name == "Blue Ridge Dental", "Company search mapping failed.");
try
{
    await client.CreateTicketAsync("import:blue-ridge", 7, "Phone support", "x");
    throw new InvalidOperationException("Tickets must require a numeric ConnectWise company ID.");
}
catch (ArgumentException)
{
}
var callStart = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);
await client.CreateTimeEntryAsync("48213", "tgifol", callStart, callStart.AddMinutes(12), "Printer call");
Assert(handler.SawExpectedTimeEntry, "Time entry request was incorrect.");
try
{
    await client.CreateTimeEntryAsync("48213", "bad member\"", callStart, callStart.AddMinutes(1), "x");
    throw new InvalidOperationException("Unsafe member IDs must be rejected.");
}
catch (ArgumentException)
{
}
Assert(client.CompanyUrl("101").StartsWith("https://na.example.connectwise.net/", StringComparison.Ordinal), "Company deep link was incorrect.");

var platformSettings = new AppSettings
{
    ConnectWisePlatformBaseUrl = ConnectWisePlatformClient.NorthAmericaBaseUrl,
    ConnectWisePlatformClientId = "platform-client-smoke",
    ConnectWisePlatformClientSecret = testOnlyCredential,
    ConnectWisePlatformScopes = "platform.companies.read, platform.tickets.create"
};
var platformHandler = new FakePlatformHandler(testOnlyCredential, exhaustAfterRequest: true);
using var platform = new ConnectWisePlatformClient(platformSettings, platformHandler);
Assert((await platform.TestAsync()).Success, "Platform OAuth token request failed.");
Assert((await platform.TestAsync()).Success, "Cached Platform OAuth token failed.");
Assert(platformHandler.TokenRequests == 1, "Platform OAuth token was not reused until expiry.");
using (var platformRequest = new HttpRequestMessage(HttpMethod.Get, $"{ConnectWisePlatformClient.NorthAmericaBaseUrl}/v1/companies"))
using (var platformResponse = await platform.SendAsync(platformRequest))
{
    Assert(platformResponse.IsSuccessStatusCode, "Authorized Platform request failed.");
}
Assert(platformHandler.SawBearerToken, "Platform Bearer token was not sent.");
try
{
    using var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, "https://attacker.example.invalid/collect");
    using var _ = await platform.SendAsync(foreignOrigin);
    throw new InvalidOperationException("Platform credentials must never be sent to a foreign origin.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("different origin", StringComparison.OrdinalIgnoreCase))
{
}
try
{
    using var blockedRequest = new HttpRequestMessage(HttpMethod.Get, $"{ConnectWisePlatformClient.NorthAmericaBaseUrl}/v1/tickets");
    using var _ = await platform.SendAsync(blockedRequest);
    throw new InvalidOperationException("Exhausted Platform quota should pause calls until reset.");
}
catch (HttpRequestException ex) when (ex.Message.Contains("quota is exhausted", StringComparison.OrdinalIgnoreCase))
{
}

var limitedSettings = new AppSettings
{
    ConnectWisePlatformBaseUrl = ConnectWisePlatformClient.NorthAmericaBaseUrl,
    ConnectWisePlatformClientId = "platform-client-429-smoke",
    ConnectWisePlatformClientSecret = testOnlyCredential,
    ConnectWisePlatformScopes = "platform.companies.read"
};
using var limited = new ConnectWisePlatformClient(limitedSettings, new FakePlatformHandler(testOnlyCredential, return429: true));
try
{
    using var limitedRequest = new HttpRequestMessage(HttpMethod.Get, $"{ConnectWisePlatformClient.NorthAmericaBaseUrl}/v1/companies");
    using var _ = await limited.SendAsync(limitedRequest);
    throw new InvalidOperationException("Platform 429 response should be surfaced with a reset time.");
}
catch (HttpRequestException ex) when (ex.Message.Contains("429 Too Many Requests", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("paused until", StringComparison.OrdinalIgnoreCase))
{
}

try
{
    using var invalid = new ConnectWiseClient(new AppSettings
    {
        ConnectWiseSite = "http://insecure.example.com",
        ConnectWiseCompanyId = "x",
        ConnectWisePublicKey = "x",
        ConnectWisePrivateKey = "x",
        ConnectWiseClientId = "x"
    });
    throw new InvalidOperationException("Insecure ConnectWise URLs must be rejected.");
}
catch (ArgumentException)
{
}

// ---- Saving a caller's number to a PSA contact
var contactHandler = new FakeContactPhoneHandler();
using (var contactClient = new ConnectWiseClient(settings, contactHandler))
{
    var phoneTypes = await contactClient.GetPhoneTypesAsync();
    Assert(phoneTypes.Count == 2 && phoneTypes[0] == new ConnectWisePhoneType(2, "Direct"), "PSA phone types should load.");
    var companyContacts = await contactClient.GetCompanyContactsAsync("101");
    Assert(companyContacts.Count == 1 && companyContacts[0].Id == "202" && companyContacts[0].Name == "Avery Stone", "Company contacts should load for the chosen company.");
    await contactClient.AddContactPhoneAsync("202", 2, "+1 (732) 297-7575");
    Assert(contactHandler.SawAddPhone, "Adding a phone should post a Phone communication with 10 digits to the contact.");
    var newContactId = await contactClient.CreateContactAsync("101", "Sonia", "Vaidya", 2, "732-297-7575");
    Assert(newContactId == "303" && contactHandler.SawCreateContact, "Creating a contact should include the company and the phone number.");
    try { await contactClient.AddContactPhoneAsync("202", 2, "101"); throw new InvalidOperationException("Extensions must not be saved as customer phone numbers."); }
    catch (ArgumentException) { }
    try { await contactClient.GetCompanyContactsAsync("101 or 1=1"); throw new InvalidOperationException("Company IDs must be numeric."); }
    catch (ArgumentException) { }
}
Assert(ConnectWiseClient.PsaPhoneValue("+44 20 7946 0958") == "442079460958", "International numbers keep their country code.");
Assert(ConnectWiseTicketing.CanSavePhoneNumbers(settings) && !ConnectWiseTicketing.CanSavePhoneNumbers(new AppSettings { ConnectWiseTicketingMode = ConnectWiseTicketingMode.Platform }), "Saving numbers needs the PSA connection.");
// ---- Slow PSA ticket creation: the ticket was created but the response timed out
ConnectWiseClient.CreateTicketTimeout = TimeSpan.FromMilliseconds(300);
var slowHandler = new SlowCreateHandler();
using (var slowClient = new ConnectWiseClient(settings, slowHandler))
{
    var recovered = await slowClient.CreateTicketAsync("101", 27, "Phone support - Acme", "Caller needs help.");
    Assert(recovered.GetProperty("id").GetInt32() == 9100, "A timed-out create should return the ticket PSA actually created.");
    Assert(slowHandler.CreateAttempts == 1, "A timed-out create must not be retried automatically.");
    Assert(slowHandler.SawLookup, "The duplicate check should look for the same company, board, and summary.");
}
ConnectWiseClient.CreateTicketTimeout = TimeSpan.FromMinutes(2);
// ---- Platform ticketing
const string companyUuid = "11111111-1111-1111-1111-111111111111";
const string ticketUuid = "22222222-2222-2222-2222-222222222222";
const string boardUuid = "33333333-3333-3333-3333-333333333333";
const string sourceUuid = "44444444-4444-4444-4444-444444444444";
var ticketingHandler = new FakePlatformTicketingHandler();
var ticketingSettings = new AppSettings
{
    ConnectWiseTicketingMode = ConnectWiseTicketingMode.Platform,
    ConnectWisePlatformBaseUrl = ConnectWisePlatformClient.NorthAmericaBaseUrl,
    ConnectWisePlatformClientId = "platform-client",
    ConnectWisePlatformClientSecret = "platform-ticketing-" + testOnlyCredential,
    ConnectWisePlatformScopes = ConnectWisePlatformClient.TicketingScopes,
    ConnectWisePlatformBoardId = boardUuid,
    ConnectWisePlatformSourceId = sourceUuid
};
using (var ticketingPlatform = new ConnectWisePlatformClient(ticketingSettings, ticketingHandler))
{
    var platformCompanies = await ticketingPlatform.GetCompaniesAsync(refresh: true);
    Assert(platformCompanies.Count == 1 && platformCompanies[0].Id == companyUuid && platformCompanies[0].Name == "Acme Platform", "Platform company mapping failed.");
    Assert(platformCompanies[0].Phones.SequenceEqual(new[] { "+19085550100", "+19085550199" }), "Platform company phones should include the primary contact and primary site numbers.");
    Assert((await ticketingPlatform.ResolveCompanyAsync("101", null))?.Id == companyUuid, "A PSA company ID should resolve to the platform company through its external ID.");
    Assert((await ticketingPlatform.ResolveCompanyAsync("999", "acme platform"))?.Id == companyUuid, "An unmapped PSA company should resolve by exact name.");

    var platformTickets = await ticketingPlatform.GetOpenTicketsAsync(companyUuid);
    Assert(ticketingHandler.SawOpenTicketQuery, "Platform open-ticket query should filter by company and exclude closed statuses.");
    Assert(platformTickets.Count == 1 && platformTickets[0].Id == ticketUuid && platformTickets[0].DisplayNumber == "5150" && platformTickets[0].Status == "In Progress", "Platform ticket mapping failed.");

    var createdPlatform = await ticketingPlatform.CreateTicketAsync(companyUuid, boardUuid, sourceUuid, "Phone support", "Caller needs help.");
    Assert(ticketingHandler.SawCreateTicket && createdPlatform.Id == ticketUuid && createdPlatform.DisplayNumber == "5151", "Platform ticket creation payload or parsing failed.");

    await ticketingPlatform.AddTicketNoteAsync(ticketUuid, "Called back.");
    Assert(ticketingHandler.SawInternalNote, "Platform notes must be posted as partner-only (visibility 2).");

    var boards = await ticketingPlatform.GetServiceBoardsAsync();
    Assert(boards.Count == 1 && boards[0].Id == boardUuid, "Inactive boards should be hidden and active boards listed.");
    try { await ticketingPlatform.AddTicketNoteAsync("48213", "x"); throw new InvalidOperationException("Numeric ticket IDs must not be sent to the platform."); }
    catch (ArgumentException) { }
}
// ---- Caller devices (Platform RMM data)
var devicesSettings = new AppSettings
{
    ConnectWiseTicketingMode = ConnectWiseTicketingMode.Psa,
    ConnectWisePlatformShowDevices = true,
    ConnectWisePlatformBaseUrl = ConnectWisePlatformClient.NorthAmericaBaseUrl,
    ConnectWisePlatformClientId = "platform-client",
    ConnectWisePlatformClientSecret = "platform-ticketing-" + testOnlyCredential,
    ConnectWisePlatformScopes = ConnectWisePlatformClient.TicketingScopes + " " + ConnectWisePlatformClient.DevicesScope
};
using (var devicePlatform = new ConnectWisePlatformClient(devicesSettings, new FakePlatformTicketingHandler()))
{
    var devices = await devicePlatform.GetCompanyDevicesAsync(companyUuid);
    Assert(devices.Count == 3, "All managed devices at the company should be listed.");
    var frontDesk = devices.Single(device => device.Name == "FRONTDESK-01");
    Assert(frontDesk.Online == true && frontDesk.LastUser == "ACME\\astone" && frontDesk.Os == "Windows 11 Pro", "Online status, last user, and OS should be merged onto each device.");
    Assert(devices.Single(device => device.Name == "LAPTOP-7").Online == false, "Offline devices should report offline.");
    Assert(devices.Single(device => device.Name == "PRINT-SRV").Online is null, "Devices missing from the heartbeat should show an unknown status.");
}
var ranked = ConnectWiseTicketing.RankDevices(
[
    new ConnectWisePlatformClient.PlatformDevice("1", "LAPTOP-7", "", false, "ACME\\jdoe"),
    new ConnectWisePlatformClient.PlatformDevice("2", "PRINT-SRV", "", true, ""),
    new ConnectWisePlatformClient.PlatformDevice("3", "FRONTDESK-01", "", true, "ACME\\astone")
], "Avery Stone");
Assert(ranked[0].Name == "FRONTDESK-01" && ranked[0].LikelyCaller && !ranked[1].LikelyCaller && ranked[1].Name == "PRINT-SRV", "The caller's own device should be first, then online devices.");
Assert(ConnectWiseTicketing.CanShowDevices(devicesSettings) && !ConnectWiseTicketing.CanShowDevices(new AppSettings()), "Devices show only when enabled and Platform is connected.");

var timeNote = ConnectWisePlatformClient.TimeNoteText("tgifol", new DateTimeOffset(2026, 9, 17, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 17, 14, 12, 0, TimeSpan.Zero), "Reset password");
Assert(timeNote.Contains("12 min") && timeNote.Contains("tgifol") && timeNote.EndsWith("Reset password"), "Platform time note text is wrong.");
Assert(!ConnectWiseTicketing.RecordsTimeAsEntry(ticketUuid) && ConnectWiseTicketing.RecordsTimeAsEntry("48213"), "Time should be a PSA entry only for numeric PSA tickets.");
Assert(ConnectWiseClient.IsCompanyId(companyUuid) && ConnectWiseClient.IsCompanyId("101") && !ConnectWiseClient.IsCompanyId("101 or 1=1"), "Company IDs must be numeric PSA IDs or platform UUIDs.");
Assert(ConnectWiseTicketing.TicketCreationProblem(ticketingSettings) is null, "Configured platform ticketing should allow ticket creation.");
Assert(ConnectWiseTicketing.TicketCreationProblem(new AppSettings { ConnectWiseTicketingMode = "Platform", ConnectWisePlatformBaseUrl = ConnectWisePlatformClient.NorthAmericaBaseUrl, ConnectWisePlatformClientId = "a", ConnectWisePlatformClientSecret = "b", ConnectWisePlatformScopes = "c" }) is not null, "Platform ticket creation needs a board and source.");
Assert(ConnectWiseTicketingMode.Normalize("bogus") == ConnectWiseTicketingMode.Psa, "Unknown ticketing modes should fall back to PSA.");
Console.WriteLine("ConnectWise PSA, Platform OAuth, and Platform ticketing tests passed: authentication, token reuse, rate-limit pause, contact mapping, deep links, and ticket payloads.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeConnectWiseHandler : HttpMessageHandler
{
    public bool SawValidAuthentication { get; private set; }
    public bool SawExpectedTicket { get; private set; }
    public bool SawExpectedTicketQuery { get; private set; }
    public bool SawExpectedTicketNote { get; private set; }
    public bool SawExpectedTimeEntry { get; private set; }
    public bool SawExpectedCompanySearch { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SawValidAuthentication |= request.Headers.Authorization?.Scheme == "Basic"
            && !string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter)
            && request.Headers.TryGetValues("clientId", out var clientIds)
            && clientIds.Single() == "client-id";

        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Get && path.EndsWith("/company/companies", StringComparison.Ordinal))
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (!query.Contains("conditions=", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[]");
            SawExpectedCompanySearch = query.Contains("name like \"Blue Ridge*\" and deletedFlag=false", StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, """[{"id":101,"name":"Blue Ridge Dental"}]""");
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/company/contacts", StringComparison.Ordinal))
        {
            const string body = """
            [{
              "id": 202,
              "firstName": "Avery",
              "lastName": "Stone",
              "company": { "id": 101, "name": "Acme Widgets" },
              "communicationItems": [
                { "type": "Phone", "value": "+1 (908) 555-0100" },
                { "communicationType": "Mobile", "value": "908-555-0101" },
                { "type": "Fax", "value": "908-555-0199" },
                { "type": "Email", "value": "avery@example.com" }
              ]
            }]
            """;
            return Json(HttpStatusCode.OK, body);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/time/entries", StringComparison.Ordinal))
        {
            using var entry = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = entry.RootElement;
            SawExpectedTimeEntry = root.GetProperty("chargeToId").GetInt64() == 48213
                && root.GetProperty("chargeToType").GetString() == "ServiceTicket"
                && root.GetProperty("member").GetProperty("identifier").GetString() == "tgifol"
                && root.GetProperty("timeStart").GetString() == "2026-09-15T14:00:00Z"
                && root.GetProperty("timeEnd").GetString() == "2026-09-15T14:12:00Z";
            return Json(HttpStatusCode.Created, "{\"id\":7}");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/service/tickets/48213/notes", StringComparison.Ordinal))
        {
            using var note = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            SawExpectedTicketNote = note.RootElement.GetProperty("text").GetString() == "Printer back online after driver rollback."
                && note.RootElement.GetProperty("internalAnalysisFlag").GetBoolean();
            return Json(HttpStatusCode.Created, "{\"id\":1}");
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/service/tickets", StringComparison.Ordinal))
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            SawExpectedTicketQuery = query.Contains("conditions=company/id=101 and closedFlag=false", StringComparison.Ordinal)
                && query.Contains("pageSize=5", StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, """[{"id":48213,"summary":"Front desk printer offline","priority":{"name":"Priority 2 - High"},"status":{"name":"New"}}]""");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/service/tickets", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            SawExpectedTicket = root.GetProperty("summary").GetString() == "Phone support"
                && root.GetProperty("company").GetProperty("id").GetInt32() == 101
                && root.GetProperty("board").GetProperty("id").GetInt32() == 7
                && root.GetProperty("initialDescription").GetString() == "Caller needs assistance.";
            return Json(HttpStatusCode.Created, "{\"id\":9001}");
        }

        return Json(HttpStatusCode.NotFound, "{\"message\":\"unexpected request\"}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}

sealed class FakePlatformHandler(string expectedClientSecret, bool exhaustAfterRequest = false, bool return429 = false) : HttpMessageHandler
{
    public int TokenRequests { get; private set; }
    public bool SawBearerToken { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Post && path == "/v1/token")
        {
            TokenRequests++;
            Assert(request.Content?.Headers.ContentType?.MediaType == "application/json", "Platform token request must use JSON.");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert(root.GetProperty("grant_type").GetString() == "client_credentials", "Platform grant type was incorrect.");
            Assert(root.GetProperty("client_id").GetString()!.StartsWith("platform-client-", StringComparison.Ordinal), "Platform Client ID was incorrect.");
            Assert(root.GetProperty("client_secret").GetString() == expectedClientSecret, "Platform Client Secret was incorrect.");
            Assert(root.GetProperty("scope").GetString()!.Contains("platform.companies.read", StringComparison.Ordinal), "Platform scopes were incorrect.");
            return Json(HttpStatusCode.OK, "{\"access_token\":\"bearer-smoke-token\",\"expires_in\":3600}");
        }

        SawBearerToken = request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "bearer-smoke-token";
        if (return429)
        {
            var limited = Json(HttpStatusCode.TooManyRequests, "{\"message\":\"quota exhausted\"}");
            limited.Headers.TryAddWithoutValidation("Limit", "500");
            limited.Headers.TryAddWithoutValidation("Remaining", "0");
            limited.Headers.TryAddWithoutValidation("Reset", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString());
            return limited;
        }

        var response = Json(HttpStatusCode.OK, "[]");
        response.Headers.TryAddWithoutValidation("Limit", "500");
        response.Headers.TryAddWithoutValidation("Remaining", exhaustAfterRequest ? "0" : "499");
        response.Headers.TryAddWithoutValidation("Reset", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString());
        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class FakePlatformTicketingHandler : HttpMessageHandler
{
    public bool SawOpenTicketQuery { get; private set; }
    public bool SawCreateTicket { get; private set; }
    public bool SawInternalNote { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path == "/v1/token") return Json("""{"access_token":"platform-ticketing-token","expires_in":3599}""");
        if (request.Headers.Authorization?.Parameter != "platform-ticketing-token") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        switch (path)
        {
            case "/api/platform/v1/company/companies":
                return Json("""
                [{"id":"11111111-1111-1111-1111-111111111111","name":"Acme Platform","externalIds":[{"externalId":"101"}],
                  "primaryContact":{"id":"55555555-5555-5555-5555-555555555555","firstName":"Avery","lastName":"Stone","primaryPhoneNumber":{"countryCode":"1","nationalNumber":"9085550100","activeFlag":true}},
                  "primarySite":{"primaryPhoneNumber":{"countryCode":"+1","nationalNumber":"9085550199"}}},
                 {"id":"not-a-uuid","name":"Broken"}]
                """);
            case "/api/platform/v2/device/categories/platform/endpoints" when request.Method == HttpMethod.Post:
                return Json("""
                {"platform":[{"companyID":"11111111-1111-1111-1111-111111111111","siteID":"s1","endpoints":[
                  {"endpointID":"e1","deviceName":"frontdesk-01","friendlyName":"FRONTDESK-01","os":{"product":"Windows 11 Pro"}},
                  {"endpointID":"e2","deviceName":"LAPTOP-7","os":{"product":"Windows 10 Pro"}},
                  {"endpointID":"e3","deviceName":"PRINT-SRV"}]}]}
                """);
            case "/api/platform/v2/device/endpoints/heartbeat":
                return Json("""{"status":"success","successfulRecords":[{"companyID":"11111111-1111-1111-1111-111111111111","endpoints":[{"EndpointID":"e1","Availability":true},{"EndpointID":"e2","Availability":false}]}],"failedRecords":[]}""");
            case "/api/platform/v2/device/endpoints/systemstate":
                return Json("""{"status":"success","successfulRecords":[{"companyID":"11111111-1111-1111-1111-111111111111","endpoints":[{"endpointID":"e1","lastLoggedOnUser":{"username":"ACME\\astone"}}]}]}""");
            case "/api/platform/v1/service/ticketing/statuses":
                return Json("""[{"id":"66666666-6666-6666-6666-666666666666","name":"Closed","category":"Closed"},{"id":"77777777-7777-7777-7777-777777777777","name":"In Progress","category":"InProgress"}]""");
            case "/api/platform/v1/service/ticketing/service-boards":
                return Json("""[{"id":"33333333-3333-3333-3333-333333333333","name":"Help Desk"},{"id":"88888888-8888-8888-8888-888888888888","name":"Old","inactiveFlag":true}]""");
            case "/api/platform/v2/service/ticketing/tickets" when request.Method == HttpMethod.Get:
                SawOpenTicketQuery = query.Contains("companyIds=11111111-1111-1111-1111-111111111111")
                    && query.Contains("statusIds=[notIn],66666666-6666-6666-6666-666666666666")
                    && query.Contains("pageSize=5") && query.Contains("pageNum=1");
                return Json("""{"tickets":[{"id":"22222222-2222-2222-2222-222222222222","number":"5150","summary":"Printer down","status":{"name":"In Progress"},"priority":{"name":"High"}}],"totalCount":1}""");
            case "/api/platform/v2/service/ticketing/tickets" when request.Method == HttpMethod.Post:
                using (var doc = JsonDocument.Parse(body))
                {
                    var r = doc.RootElement;
                    SawCreateTicket = r.GetProperty("summary").GetString() == "Phone support"
                        && r.GetProperty("description").GetString() == "Caller needs help."
                        && r.GetProperty("serviceBoard").GetProperty("id").GetString() == "33333333-3333-3333-3333-333333333333"
                        && r.GetProperty("source").GetProperty("id").GetString() == "44444444-4444-4444-4444-444444444444"
                        && r.GetProperty("company").GetProperty("id").GetString() == "11111111-1111-1111-1111-111111111111";
                }
                return Json("""{"id":"22222222-2222-2222-2222-222222222222","number":"5151"}""", HttpStatusCode.Created);
            case "/api/platform/v1/service/ticketing/tickets/22222222-2222-2222-2222-222222222222/notes":
                using (var doc = JsonDocument.Parse(body))
                    SawInternalNote = doc.RootElement.GetProperty("visibility").GetInt32() == 2 && doc.RootElement.GetProperty("detail").GetString() == "Called back.";
                return Json("{}", HttpStatusCode.Created);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

sealed class SlowCreateHandler : HttpMessageHandler
{
    public int CreateAttempts { get; private set; }
    public bool SawLookup { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/service/tickets"))
        {
            CreateAttempts++;
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        SawLookup = query.Contains("company/id=101") && query.Contains("board/id=27") && query.Contains("summary=\"Phone support - Acme\"") && query.Contains("dateEntered>=[");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"id":9100,"summary":"Phone support - Acme"}]""", Encoding.UTF8, "application/json") };
    }
}

sealed class FakeContactPhoneHandler : HttpMessageHandler
{
    public bool SawAddPhone { get; private set; }
    public bool SawCreateContact { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path.EndsWith("/company/communicationTypes") && query.Contains("phoneFlag=true"))
            return Json("""[{"id":2,"description":"Direct"},{"id":4,"description":"Mobile"}]""");
        if (path.EndsWith("/company/contacts") && request.Method == HttpMethod.Get && query.Contains("company/id=101"))
            return Json("""[{"id":202,"firstName":"Avery","lastName":"Stone","communicationItems":[{"type":{"name":"Direct"},"communicationType":"Phone","value":"9085550100"}]}]""");
        if (path.EndsWith("/company/contacts/202/communications") && request.Method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            SawAddPhone = r.GetProperty("value").GetString() == "7322977575" && r.GetProperty("communicationType").GetString() == "Phone" && r.GetProperty("type").GetProperty("id").GetInt32() == 2;
            return Json("{}", HttpStatusCode.Created);
        }
        if (path.EndsWith("/company/contacts") && request.Method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var item = r.GetProperty("communicationItems")[0];
            SawCreateContact = r.GetProperty("firstName").GetString() == "Sonia" && r.GetProperty("company").GetProperty("id").GetInt64() == 101
                && item.GetProperty("value").GetString() == "7322977575" && item.GetProperty("communicationType").GetString() == "Phone";
            return Json("""{"id":303}""", HttpStatusCode.Created);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}