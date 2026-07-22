using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CallBridge.Service.Tests;

public sealed class ApiSecurityTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    [Fact]
    public async Task HealthIsPublicButOperationalRoutesRequireAuthentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/diagnostics")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ServiceFactory.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/diagnostics")).StatusCode);
    }

    [Fact]
    public async Task ForeignHostAndBrowserOriginAreRejected()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ServiceFactory.Token);

        using var badHost = new HttpRequestMessage(HttpMethod.Get, "/diagnostics");
        badHost.Headers.Host = "attacker.example.invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(badHost)).StatusCode);

        using var badOrigin = new HttpRequestMessage(HttpMethod.Get, "/diagnostics");
        badOrigin.Headers.TryAddWithoutValidation("Origin", "https://attacker.example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(badOrigin)).StatusCode);
    }

    [Fact]
    public async Task DirectoryCallsNotesExportAndDeletionRoundTrip()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ServiceFactory.Token);

        var contact = new
        {
            companyId = "company-1",
            companyName = "Example Company",
            contactId = "contact-1",
            contactName = "Example Contact",
            phones = new[] { "732-555-0100" }
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/directory/contacts", contact)).StatusCode);

        using var directory = JsonDocument.Parse(await client.GetStringAsync("/directory"));
        Assert.Single(directory.RootElement.GetProperty("records").EnumerateArray());

        var call = new
        {
            callId = "call-1",
            state = "started",
            direction = "outbound",
            callerNumber = "201",
            calledNumber = "7325550100",
            extension = "201",
            occurredAt = "2026-07-19T12:00:00Z"
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/calls/events", call)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/calls/events", call with { state = "hangup", occurredAt = "2026-07-19T12:02:05Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/calls/call-1/notes", new { notes = "Synthetic note", outcome = "Resolved" })).StatusCode);

        using var journal = JsonDocument.Parse(await client.GetStringAsync("/calls/journal"));
        var journalCall = Assert.Single(journal.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Equal(125, journalCall.GetProperty("durationSeconds").GetInt64());
        Assert.Equal("Example Company", journalCall.GetProperty("companyName").GetString());
        Assert.Equal("Synthetic note", journalCall.GetProperty("notes").GetString());

        using var export = JsonDocument.Parse(await client.GetStringAsync("/data/export"));
        Assert.Equal("sensitive-customer-data", export.RootElement.GetProperty("classification").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/data/calls")).StatusCode);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/data/calls");
        delete.Headers.Add("X-CallBridge-Confirm", "delete-call-history");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(delete)).StatusCode);
    }
}
