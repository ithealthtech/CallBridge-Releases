using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using CallBridge.Service;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var port = int.TryParse(configuration["PORT"], out var configuredPort) ? configuredPort : 8787;
var localToken = configuration["CALLBRIDGE_LOCAL_TOKEN"] ?? "";
var databasePath = configuration["DATABASE_PATH"] ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "IT Health Technologies", "CallBridge", "data", "callbridge.db");
var retentionDays = int.TryParse(configuration["CALLBRIDGE_CALL_RETENTION_DAYS"], out var configuredRetention)
    ? Math.Clamp(configuredRetention, 1, 3650)
    : 90;

if (string.IsNullOrWhiteSpace(localToken)) throw new InvalidOperationException("CALLBRIDGE_LOCAL_TOKEN is required.");
if (port is < 1024 or > 65535) throw new InvalidOperationException("PORT must be between 1024 and 65535.");

builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1_048_576;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.MaxDepth = 32);
builder.Services.AddSingleton(_ => new CallBridgeStore(databasePath));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "local",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
var store = app.Services.GetRequiredService<CallBridgeStore>();
store.ApplyRetention(retentionDays);

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";

    if (!IsLoopbackHost(context.Request.Host.Host))
    {
        await WriteError(context, StatusCodes.Status400BadRequest, "invalid_host");
        return;
    }

    if (context.Request.Headers.Origin is { Count: > 0 } origins &&
        origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsLoopback))
    {
        await WriteError(context, StatusCodes.Status403Forbidden, "origin_not_allowed");
        return;
    }

    if (!context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
        !TokenMatches(context.Request.Headers.Authorization, localToken))
    {
        await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
        return;
    }

    try
    {
        await next();
    }
    catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        if (!context.Response.HasStarted) await WriteError(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large");
    }
    catch (Exception exception)
    {
        app.Logger.LogError("Request {Method} {Path} failed with {ExceptionType}", context.Request.Method, context.Request.Path, exception.GetType().Name);
        if (!context.Response.HasStarted) await WriteError(context, StatusCodes.Status500InternalServerError, "internal_error");
    }
});

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "callbridge-service", version = "0.13.0", telephony = "desktop-managed" }));
app.MapGet("/capabilities", () => Results.Ok(new
{
    ok = true,
    telephony = new { mode = "desktop", realCalling = true, registration = true, callControl = true, dtmf = true, transfer = true },
    directory = new { read = true, import = true, manage = true },
    callHistory = true,
    messages = false,
    voicemail = false,
    parking = false,
    recordings = false
}));
app.MapGet("/providers", () => Results.Ok(new { providers = new[] { "standard-sip" }, planned = Array.Empty<string>() }));
app.MapGet("/diagnostics", () => Results.Ok(new { ok = true, config = new { host = "127.0.0.1", port, retentionDays }, database = store.Diagnostics(retentionDays) }));

app.MapGet("/directory", (int? limit) => Results.Ok(new { ok = true, source = "live", records = store.Directory(limit ?? 200) }));
app.MapPut("/directory", (DirectoryPayload payload) => Validate(() =>
{
    var records = InputValidation.DirectoryRecords(payload);
    return Results.Ok(new { ok = true, source = "user-import", imported = records.Count, records = store.ReplaceDirectory(records, "user-import") });
}));
app.MapPut("/integrations/connectwise/directory", (DirectoryPayload payload) => Validate(() =>
{
    var records = InputValidation.DirectoryRecords(payload);
    return Results.Ok(new { ok = true, source = "connectwise-psa", imported = records.Count, records = store.ReplaceDirectory(records, "connectwise-psa") });
}));
app.MapDelete("/directory", () => Results.Ok(new { ok = true, source = "user-import", deleted = store.ClearImportedDirectory() }));
app.MapPost("/directory/contacts", (ContactInput input) => Validate(() =>
{
    var contact = InputValidation.Contact(input);
    return Results.Created($"/directory/contacts/{Uri.EscapeDataString(contact.ContactId!)}", new { ok = true, contact, records = store.UpsertContact(contact) });
}));
app.MapPut("/directory/contacts/{contactId}", (string contactId, ContactInput input) => Validate(() =>
{
    var contact = InputValidation.Contact(input, contactId);
    return Results.Ok(new { ok = true, contact, records = store.UpsertContact(contact) });
}));
app.MapDelete("/directory/contacts/{contactId}", (string contactId) =>
{
    var deleted = store.DeleteContact(contactId);
    return deleted > 0 ? Results.Ok(new { ok = true, deleted }) : Results.NotFound(new { ok = false, deleted = 0 });
});
app.MapGet("/matches", (string? phone) => Results.Ok(new { phone = phone ?? "", matches = store.FindByPhone(phone) }));

app.MapGet("/events/recent", (int? limit) => Results.Ok(new { events = store.RecentEvents(limit ?? 50) }));
app.MapGet("/calls/journal", (int? limit) => Results.Ok(new { ok = true, calls = store.CallJournal(limit ?? 50) }));
app.MapPost("/calls/events", (CallEventInput input) => Validate(() =>
{
    var callEvent = InputValidation.CallEvent(input);
    store.ApplyRetention(retentionDays);
    return Results.Created("/calls/journal", new { ok = true, @event = store.SaveCallEvent(callEvent) });
}));
app.MapPut("/calls/{callId}/notes", (string callId, CallNoteInput input) =>
{
    var note = store.SaveCallNote(callId, input.Notes, input.Outcome);
    return note is null ? Results.NotFound(new { ok = false, error = "call_not_found" }) : Results.Ok(new { ok = true, note });
});

app.MapGet("/data/export", () => Results.Ok(new
{
    generatedAt = DateTimeOffset.UtcNow,
    classification = "sensitive-customer-data",
    directory = store.Directory(5000),
    calls = store.CallJournal(200)
}));
app.MapDelete("/data/calls", (HttpContext context) =>
{
    if (!string.Equals(context.Request.Headers["X-CallBridge-Confirm"], "delete-call-history", StringComparison.Ordinal))
        return Results.BadRequest(new { ok = false, error = "confirmation_required" });
    return Results.Ok(new { ok = true, deleted = store.ClearCallHistory() });
});
app.MapGet("/audit/recent", (int? limit) => Results.Ok(new { events = store.RecentAudit(limit ?? 50) }));

app.Run();

static IResult Validate(Func<IResult> action)
{
    try { return action(); }
    catch (ApiValidationException exception) { return Results.BadRequest(new { ok = false, error = exception.Code }); }
}

static bool IsLoopbackHost(string host)
{
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
    return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

static bool TokenMatches(string? authorization, string expectedToken)
{
    if (string.IsNullOrWhiteSpace(authorization) || string.IsNullOrEmpty(expectedToken)) return false;
    var expected = Encoding.UTF8.GetBytes("Bearer " + expectedToken);
    var actual = Encoding.UTF8.GetBytes(authorization);
    return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
}

static async Task WriteError(HttpContext context, int status, string code)
{
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new { ok = false, error = code });
}

public partial class Program;
