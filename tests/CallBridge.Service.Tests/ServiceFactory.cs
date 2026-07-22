using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CallBridge.Service.Tests;

public sealed class ServiceFactory : WebApplicationFactory<Program>
{
    public const string Token = "integration-test-local-token";
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "callbridge-service-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_tempDirectory);
        builder.UseSetting("CALLBRIDGE_LOCAL_TOKEN", Token);
        builder.UseSetting("DATABASE_PATH", Path.Combine(_tempDirectory, "callbridge.db"));
        builder.UseSetting("CALLBRIDGE_CALL_RETENTION_DAYS", "90");
        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
    }
}
