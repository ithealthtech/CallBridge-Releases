namespace CallBridge.Service.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "callbridge-store-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CallBridgeStore _store;

    public StoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new CallBridgeStore(Path.Combine(_directory, "store.db"));
    }

    [Fact]
    public void PhoneNormalizationProducesExpectedVariants()
    {
        Assert.Equal("+17325550100", PhoneNumbers.Normalize("(732) 555-0100"));
        Assert.Contains("7325550100", PhoneNumbers.Variants("+1 732 555 0100"));
    }

    [Fact]
    public void RetentionDeletesExpiredCallsAndPreservesRecentCalls()
    {
        _store.SaveCallEvent(InputValidation.CallEvent(new("old", "started", "outbound", "201", "7325550100", "201", DateTimeOffset.UtcNow.AddDays(-100))));
        _store.SaveCallEvent(InputValidation.CallEvent(new("recent", "started", "outbound", "201", "7325550101", "201", DateTimeOffset.UtcNow)));
        Assert.Equal(1, _store.ApplyRetention(90));
        Assert.Single(_store.CallJournal(10));
        Assert.Equal("recent", _store.CallJournal(10)[0].CallId);
    }

    [Fact]
    public void SimultaneousCallLegsRemainIndependentInTheJournal()
    {
        var started = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        _store.SaveCallEvent(InputValidation.CallEvent(new("primary-leg", "started", "outbound", "201", "101", "201", started)));
        _store.SaveCallEvent(InputValidation.CallEvent(new("waiting-leg", "ringing", "inbound", "202", "201", "201", started.AddSeconds(5))));
        _store.SaveCallEvent(InputValidation.CallEvent(new("primary-leg", "hold", "outbound", "201", "101", "201", started.AddSeconds(10))));
        _store.SaveCallEvent(InputValidation.CallEvent(new("waiting-leg", "answered", "inbound", "202", "201", "201", started.AddSeconds(11))));
        _store.SaveCallEvent(InputValidation.CallEvent(new("waiting-leg", "hangup", "inbound", "202", "201", "201", started.AddSeconds(40))));
        _store.SaveCallEvent(InputValidation.CallEvent(new("primary-leg", "resume", "outbound", "201", "101", "201", started.AddSeconds(41))));
        _store.SaveCallEvent(InputValidation.CallEvent(new("primary-leg", "hangup", "outbound", "201", "101", "201", started.AddSeconds(70))));

        var calls = _store.CallJournal(10);
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, call => call.CallId == "primary-leg" && call.Direction == "outbound" && call.State == "hangup");
        Assert.Contains(calls, call => call.CallId == "waiting-leg" && call.Direction == "inbound" && call.State == "hangup");
    }

    [Fact]
    public void CallEventRetriesAreIdempotentAndConflictsAreRejected()
    {
        var occurredAt = DateTimeOffset.Parse("2026-09-02T13:00:00Z");
        var input = InputValidation.CallEvent(new(
            "call-a",
            "started",
            "outbound",
            "201",
            "101",
            "201",
            occurredAt,
            "event-a"));

        var first = _store.SaveCallEvent(input);
        var replay = _store.SaveCallEvent(input);
        Assert.Equal(first, replay);
        Assert.Single(_store.RecentEvents(10));

        var conflict = input with { State = "answered" };
        var error = Assert.Throws<ApiValidationException>(() => _store.SaveCallEvent(conflict));
        Assert.Equal("event_id_conflict", error.Code);
        Assert.Single(_store.RecentEvents(10));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
