using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CallBridge.Service.Tests;

public sealed class ApiContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "callbridge-api-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<global::Program> _factory;
    private const string Token = "test-only-local-token";

    public ApiContractTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CALLBRIDGE_LOCAL_TOKEN", Token);
            builder.UseSetting("DATABASE_PATH", Path.Combine(_directory, "api.db"));
        });
    }

    [Fact]
    public async Task CallEventEndpointIsAuthenticatedIdempotentAndRejectsConflicts()
    {
        using var unauthorizedClient = _factory.CreateClient();
        using var unauthorized = await unauthorizedClient.PostAsJsonAsync("/calls/events", Payload("started"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var created = await client.PostAsJsonAsync("/calls/events", Payload("started"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var originalEvent = await EventAsync(created);

        using var replay = await client.PostAsJsonAsync("/calls/events", Payload("started"));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalEvent.GetProperty("eventId").GetString(), (await EventAsync(replay)).GetProperty("eventId").GetString());

        using var conflict = await client.PostAsJsonAsync("/calls/events", Payload("answered"));
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        using var conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("event_id_conflict", conflictBody.RootElement.GetProperty("error").GetString());

        using var journal = await client.GetAsync("/calls/journal?limit=10");
        journal.EnsureSuccessStatusCode();
        using var journalBody = JsonDocument.Parse(await journal.Content.ReadAsStringAsync());
        Assert.Single(journalBody.RootElement.GetProperty("calls").EnumerateArray());
    }

    private static object Payload(string state) => new
    {
        eventId = "api-event-a",
        callId = "api-call-a",
        direction = "outbound",
        state,
        callerNumber = "201",
        calledNumber = "5550100",
        extension = "201",
        occurredAt = "2026-09-02T13:00:00Z"
    };

    private static async Task<JsonElement> EventAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("event").Clone();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
