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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
