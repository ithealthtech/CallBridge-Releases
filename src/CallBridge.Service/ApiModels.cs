namespace CallBridge.Service;

public sealed record DirectoryPayload(IReadOnlyList<DirectoryRecordInput>? Records);

public sealed record DirectoryRecordInput(
    string? CompanyId,
    string? CompanyName,
    string? ContactId,
    string? ContactName,
    IReadOnlyList<string>? Phones,
    string? Phone = null);

public sealed record ContactInput(
    string? CompanyId,
    string? CompanyName,
    string? ContactId,
    string? ContactName,
    IReadOnlyList<string>? Phones,
    string? Phone = null);

public sealed record CallEventInput(
    string? CallId,
    string? State,
    string? Direction,
    string? CallerNumber,
    string? CalledNumber,
    string? Extension,
    DateTimeOffset? OccurredAt,
    string? EventId = null);

public sealed record CallNoteInput(string? Notes, string? Outcome);

public sealed record DirectoryRow(
    string CompanyId,
    string CompanyName,
    string? ContactId,
    string? ContactName,
    string Phone,
    string Source);

public sealed record PhoneMatch(
    string CompanyId,
    string CompanyName,
    string? ContactId,
    string? ContactName,
    string Normalized);

public sealed record StoredCallEvent(
    string EventId,
    string Provider,
    string ProviderCallId,
    string Direction,
    string State,
    string CallerNumber,
    string CalledNumber,
    string Extension,
    string? CompanyId,
    string? ContactId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt);

public sealed record CallJournalRow(
    string CallId,
    string Provider,
    string Direction,
    string State,
    string CallerNumber,
    string CalledNumber,
    string Extension,
    string CompanyId,
    string CompanyName,
    string ContactId,
    string ContactName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long DurationSeconds,
    string Notes,
    string Outcome,
    DateTimeOffset? NotesUpdatedAt);

public sealed record CallNote(string CallId, string Notes, string Outcome, DateTimeOffset UpdatedAt);

public sealed record AuditEvent(long Id, string Action, string EntityType, string EntityId, string Actor, DateTimeOffset OccurredAt);

public sealed class ApiValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
