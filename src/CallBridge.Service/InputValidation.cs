using System.Text.RegularExpressions;

namespace CallBridge.Service;

public static partial class InputValidation
{
    public static IReadOnlyList<DirectoryRecordInput> DirectoryRecords(DirectoryPayload? payload)
    {
        if (payload?.Records is null) throw new ApiValidationException("records_must_be_an_array");
        if (payload.Records.Count > 5000) throw new ApiValidationException("directory_limit_exceeded");
        return payload.Records.Select((record, index) => NormalizeRecord(record, index + 1)).ToArray();
    }

    public static ContactInput Contact(ContactInput? input, string? routeContactId = null)
    {
        if (input is null) throw new ApiValidationException("contact_required");
        var companyName = Clean(input.CompanyName, 200);
        var contactName = Clean(input.ContactName, 200);
        var phones = Phones(input.Phones, input.Phone);
        if (companyName.Length == 0) throw new ApiValidationException("company_name_required");
        if (contactName.Length == 0) throw new ApiValidationException("contact_name_required");
        if (phones.Count == 0) throw new ApiValidationException("phone_required");
        var companyId = Clean(input.CompanyId, 200);
        if (companyId.Length == 0) companyId = "managed-company-" + Slug(companyName);
        var contactId = Clean(routeContactId ?? input.ContactId, 200);
        if (contactId.Length == 0) contactId = Guid.NewGuid().ToString("D");
        return new(companyId, companyName, contactId, contactName, phones);
    }

    public static CallEventInput CallEvent(CallEventInput? input)
    {
        if (input is null) throw new ApiValidationException("call_event_required");
        var callId = Clean(input.CallId, 200);
        var state = Clean(input.State, 50).ToLowerInvariant();
        if (callId.Length == 0 || state.Length == 0) throw new ApiValidationException("call_id_and_state_required");
        var allowedStates = new HashSet<string>(StringComparer.Ordinal) { "ringing", "started", "answered", "hold", "resume", "transfer", "hangup", "ended", "missed", "failed" };
        if (!allowedStates.Contains(state)) throw new ApiValidationException("invalid_call_state");
        var direction = Clean(input.Direction, 20).ToLowerInvariant();
        if (direction.Length == 0) direction = "outbound";
        if (direction is not ("inbound" or "outbound")) throw new ApiValidationException("invalid_call_direction");
        return input with
        {
            CallId = callId,
            State = state,
            Direction = direction,
            CallerNumber = Clean(input.CallerNumber, 64),
            CalledNumber = Clean(input.CalledNumber, 64),
            Extension = Clean(input.Extension, 64),
            OccurredAt = input.OccurredAt ?? DateTimeOffset.UtcNow
        };
    }

    private static DirectoryRecordInput NormalizeRecord(DirectoryRecordInput input, int index)
    {
        var companyName = Clean(input.CompanyName, 200);
        var contactName = Clean(input.ContactName, 200);
        var phones = Phones(input.Phones, input.Phone);
        if (companyName.Length == 0) throw new ApiValidationException($"company_name_required_at_{index}");
        if (phones.Count == 0) throw new ApiValidationException($"phone_required_at_{index}");
        var companyId = Clean(input.CompanyId, 200);
        if (companyId.Length == 0) companyId = "import-company-" + Slug(companyName);
        var contactId = Clean(input.ContactId, 200);
        if (contactName.Length > 0 && contactId.Length == 0) contactId = $"import-contact-{companyId}-{Slug(contactName)}";
        return new(companyId, companyName, contactId.Length == 0 ? null : contactId, contactName.Length == 0 ? null : contactName, phones);
    }

    private static IReadOnlyList<string> Phones(IReadOnlyList<string>? phones, string? phone)
    {
        IReadOnlyList<string> values = phones ?? (phone is null ? Array.Empty<string>() : new[] { phone });
        if (values.Count > 20) throw new ApiValidationException("phone_limit_exceeded");
        return values.Select(value => PhoneNumbers.Normalize(value)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Clean(string? value, int limit) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim()[..Math.Min(value.Trim().Length, limit)];
    private static string Slug(string value) => SlugCharacters().Replace(value.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugCharacters();
}
