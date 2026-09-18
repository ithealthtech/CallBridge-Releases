using System.IO;
using System.Net.Http;

namespace CallBridge.Desktop;

/// <summary>Which ConnectWise API CallBridge uses for tickets.</summary>
public static class ConnectWiseTicketingMode
{
    /// <summary>ConnectWise PSA REST API (API member keys) for everything.</summary>
    public const string Psa = "PSA";
    /// <summary>ConnectWise Platform API (OAuth) for everything; callers match on company numbers.</summary>
    public const string Platform = "Platform";
    /// <summary>Platform API for tickets and notes; PSA for the contact directory, which gives richer caller matching.</summary>
    public const string PlatformWithPsa = "PlatformWithPsa";

    public static readonly (string Value, string Label)[] Choices =
    [
        (Psa, "ConnectWise PSA"),
        (Platform, "ConnectWise Platform"),
        (PlatformWithPsa, "Platform, with PSA contact lookup")
    ];

    public static string Normalize(string? value) =>
        Choices.Select(choice => choice.Value).FirstOrDefault(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase)) ?? Psa;
}

/// <summary>
/// Routes CallBridge's ticket work to the ConnectWise PSA API or the ConnectWise Platform API, depending on the
/// admin's choice. PSA IDs are numbers and platform IDs are UUIDs, so notes and time always go to the API that
/// owns the ticket they are written to.
/// </summary>
public sealed class ConnectWiseTicketing : IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _mode;
    private ConnectWiseClient? _psa;
    private ConnectWisePlatformClient? _platform;

    public ConnectWiseTicketing(AppSettings settings)
    {
        _settings = settings;
        _mode = ConnectWiseTicketingMode.Normalize(settings.ConnectWiseTicketingMode);
    }

    public string Mode => _mode;
    public bool UsesPlatform => _mode != ConnectWiseTicketingMode.Psa;
    private bool PsaConfigured => ConnectWiseClient.IsConfigured(_settings);
    private bool PlatformConfigured => ConnectWisePlatformClient.IsConfigured(_settings);
    private ConnectWiseClient Psa => _psa ??= new ConnectWiseClient(_settings);
    private ConnectWisePlatformClient PlatformClient => _platform ??= new ConnectWisePlatformClient(_settings);

    public static bool IsConfigured(AppSettings settings) =>
        ConnectWiseTicketingMode.Normalize(settings.ConnectWiseTicketingMode) == ConnectWiseTicketingMode.Psa
            ? ConnectWiseClient.IsConfigured(settings)
            : ConnectWisePlatformClient.IsConfigured(settings);

    /// <summary>What the admin still needs to set up before techs can create tickets, or null when ready.</summary>
    public static string? TicketCreationProblem(AppSettings settings)
    {
        if (!IsConfigured(settings)) return "Connect ConnectWise in Settings first.";
        if (ConnectWiseTicketingMode.Normalize(settings.ConnectWiseTicketingMode) == ConnectWiseTicketingMode.Psa)
            return settings.ConnectWiseBoardId > 0 ? null : "Ask your admin to set a default service board ID in Settings > ConnectWise.";
        return ConnectWisePlatformClient.IsPlatformId(settings.ConnectWisePlatformBoardId) && ConnectWisePlatformClient.IsPlatformId(settings.ConnectWisePlatformSourceId)
            ? null
            : "Ask your admin to choose a default service board and source in Settings > ConnectWise.";
    }

    public string ProviderLabel => _mode == ConnectWiseTicketingMode.Psa ? "ConnectWise PSA" : "ConnectWise Platform";

    /// <summary>Downloads callable records for the local directory.</summary>
    public async Task<List<ConnectWiseContactRecord>> DownloadDirectoryAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_mode == ConnectWiseTicketingMode.Psa || (_mode == ConnectWiseTicketingMode.PlatformWithPsa && PsaConfigured))
        {
            var test = await Psa.TestAsync();
            if (!test.Success) throw new HttpRequestException(test.Message);
            return await Psa.DownloadContactsAsync(progress);
        }
        progress?.Report("Downloading ConnectWise Platform companies...");
        var companies = await PlatformClient.GetCompaniesAsync(refresh: true, cancellationToken);
        return companies
            .Where(company => company.Phones.Length > 0)
            .Select(company => new ConnectWiseContactRecord(
                company.Id,
                company.Name,
                string.IsNullOrWhiteSpace(company.ContactId) ? company.Id : company.ContactId,
                string.IsNullOrWhiteSpace(company.ContactName) ? company.Name : company.ContactName,
                company.Phones))
            .ToList();
    }

    /// <summary>Open tickets for a company; returns the company ID tickets should be created under.</summary>
    public async Task<(List<ConnectWiseTicketSummary> Tickets, string CompanyId)> GetOpenTicketsAsync(string companyId, string? companyName, int maximum = 5, CancellationToken cancellationToken = default)
    {
        if (_mode == ConnectWiseTicketingMode.Psa)
            return (await Psa.GetOpenTicketsAsync(companyId, maximum, cancellationToken), companyId);
        var company = await PlatformClient.ResolveCompanyAsync(companyId, companyName, cancellationToken)
            ?? throw new ArgumentException("This company wasn't found in ConnectWise Platform.");
        return (await PlatformClient.GetOpenTicketsAsync(company.Id, maximum, cancellationToken), company.Id);
    }

    public async Task<IReadOnlyList<ConnectWiseCompanySummary>> SearchCompaniesAsync(string query, int maximum = 10, CancellationToken cancellationToken = default)
    {
        if (_mode == ConnectWiseTicketingMode.Psa) return await Psa.SearchCompaniesAsync(query, maximum, cancellationToken);
        var text = (query ?? "").Trim();
        if (text.Length is 0 or > 100) throw new ArgumentException("Enter 1 to 100 characters to search companies.", nameof(query));
        var companies = await PlatformClient.GetCompaniesAsync(false, cancellationToken);
        return companies
            .Where(company => company.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(company => company.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(company => company.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maximum, 1, 25))
            .Select(company => new ConnectWiseCompanySummary(company.Id, company.Name))
            .ToList();
    }

    public async Task<ConnectWiseTicketSummary> CreateTicketAsync(string companyId, string? companyName, string summary, string description, CancellationToken cancellationToken = default)
    {
        if (TicketCreationProblem(_settings) is { } problem) throw new InvalidOperationException(problem);
        if (_mode == ConnectWiseTicketingMode.Psa)
        {
            var created = await Psa.CreateTicketAsync(companyId, _settings.ConnectWiseBoardId, summary, description);
            var id = created.TryGetProperty("id", out var idNode) ? idNode.ToString() : "";
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("ConnectWise didn't return the new ticket's ID.");
            return new ConnectWiseTicketSummary(id, summary, "", "New");
        }
        var company = await PlatformClient.ResolveCompanyAsync(companyId, companyName, cancellationToken)
            ?? throw new ArgumentException("This company wasn't found in ConnectWise Platform. Choose it from the company search.");
        return await PlatformClient.CreateTicketAsync(company.Id, _settings.ConnectWisePlatformBoardId, _settings.ConnectWisePlatformSourceId, summary, description, cancellationToken);
    }

    public async Task AddTicketNoteAsync(string ticketId, string text, CancellationToken cancellationToken = default)
    {
        if (ConnectWisePlatformClient.IsPlatformId(ticketId))
        {
            if (!PlatformConfigured) throw new InvalidOperationException("Connect ConnectWise Platform in Settings first.");
            await PlatformClient.AddTicketNoteAsync(ticketId, text, cancellationToken);
            return;
        }
        if (!PsaConfigured) throw new InvalidOperationException("Connect ConnectWise PSA in Settings first.");
        await Psa.AddTicketNoteAsync(ticketId, text, cancellationToken);
    }

    /// <summary>True when time on this ticket is saved as a PSA time entry (which needs the tech's member ID).</summary>
    public static bool RecordsTimeAsEntry(string ticketId) => !ConnectWisePlatformClient.IsPlatformId(ticketId);

    /// <summary>Records confirmed call time: a PSA time entry, or an internal note on platform tickets (the platform API has no time entries).</summary>
    public async Task RecordTimeAsync(string ticketId, string memberIdentifier, DateTimeOffset start, DateTimeOffset end, string notes, CancellationToken cancellationToken = default)
    {
        if (RecordsTimeAsEntry(ticketId))
        {
            if (!PsaConfigured) throw new InvalidOperationException("Connect ConnectWise PSA in Settings first.");
            await Psa.CreateTimeEntryAsync(ticketId, memberIdentifier, start, end, notes, cancellationToken);
            return;
        }
        await AddTicketNoteAsync(ticketId, ConnectWisePlatformClient.TimeNoteText(memberIdentifier, start, end, notes), cancellationToken);
    }

    /// <summary>
    /// Whether a caller's number can be saved to a ConnectWise contact. Only the PSA API can update contacts;
    /// the Platform API can create contacts but not search or edit them.
    /// </summary>
    public static bool CanSavePhoneNumbers(AppSettings settings) =>
        ConnectWiseTicketingMode.Normalize(settings.ConnectWiseTicketingMode) == ConnectWiseTicketingMode.Psa && ConnectWiseClient.IsConfigured(settings);

    public const string SavePhoneUnavailableMessage =
        "Saving numbers to ConnectWise contacts needs the ConnectWise PSA connection. The Platform API can't update contacts yet, so add the number in ConnectWise.";

    /// <summary>Live PSA lookup of a number that isn't in the local directory. Null when PSA isn't connected.</summary>
    public async Task<ConnectWiseContactRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default) =>
        PsaConfigured && _mode != ConnectWiseTicketingMode.Platform ? await Psa.FindByPhoneAsync(phone, cancellationToken) : null;

    public Task<List<ConnectWisePhoneType>> GetPhoneTypesAsync(CancellationToken cancellationToken = default) => Psa.GetPhoneTypesAsync(cancellationToken);
    public Task<List<ConnectWiseContactSummary>> GetCompanyContactsAsync(string companyId, CancellationToken cancellationToken = default) => Psa.GetCompanyContactsAsync(companyId, cancellationToken);
    public Task AddContactPhoneAsync(string contactId, int phoneTypeId, string phone, CancellationToken cancellationToken = default) => Psa.AddContactPhoneAsync(contactId, phoneTypeId, phone, cancellationToken);
    public Task<string> CreateContactAsync(string companyId, string firstName, string lastName, int phoneTypeId, string phone, CancellationToken cancellationToken = default) =>
        Psa.CreateContactAsync(companyId, firstName, lastName, phoneTypeId, phone, cancellationToken);

    /// <summary>The caller's company devices come from ConnectWise Platform, whichever ticketing connection is chosen.</summary>
    public static bool CanShowDevices(AppSettings settings) =>
        settings.ConnectWisePlatformShowDevices && ConnectWisePlatformClient.IsConfigured(settings);

    /// <summary>
    /// The company's managed devices, best matches first: devices whose last signed-in user looks like the caller.
    /// </summary>
    public async Task<CallerDevices> GetCallerDevicesAsync(string companyId, string? companyName, string? contactName, int maximum = 6, CancellationToken cancellationToken = default)
    {
        if (!CanShowDevices(_settings)) return new([], null, 0);
        var company = await PlatformClient.ResolveCompanyAsync(companyId, companyName, cancellationToken);
        if (company is null) return new([], "This company wasn't found in ConnectWise Platform, so its devices can't be shown.", 0);
        var devices = await PlatformClient.GetCompanyDevicesAsync(company.Id, cancellationToken);
        var ranked = RankDevices(devices, contactName);
        return new(ranked.Take(Math.Clamp(maximum, 1, 50)).ToList(), ranked.Count == 0 ? "No managed devices for this company." : null, ranked.Count);
    }

    /// <summary>The caller's own devices first (last signed-in user matches their name), then online ones, then by name.</summary>
    public static List<CallerDevice> RankDevices(IEnumerable<ConnectWisePlatformClient.PlatformDevice> devices, string? contactName)
    {
        var nameParts = (contactName ?? "").Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length >= 3).ToArray();
        return devices
            .Select(device => new CallerDevice(device.Name, device.Os, device.Online, device.LastUser,
                nameParts.Length > 0 && nameParts.Any(part => device.LastUser.Contains(part, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(device => device.LikelyCaller)
            .ThenByDescending(device => device.Online == true)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A browser link for the ticket, when one is known. The platform API doesn't publish ticket web links.</summary>
    public string? TicketUrl(string ticketId) => PsaConfigured && long.TryParse(ticketId, out _) ? Psa.TicketUrl(ticketId) : null;
    public string? CompanyUrl(string companyId) => PsaConfigured && long.TryParse(companyId, out _) ? Psa.CompanyUrl(companyId) : null;

    public void Dispose()
    {
        _psa?.Dispose();
        _platform?.Dispose();
    }
}

/// <summary>A device shown in the call pop-up. LikelyCaller means its last signed-in user matches the caller's name.</summary>
public sealed record CallerDevice(string Name, string Os, bool? Online, string LastUser, bool LikelyCaller);

public sealed record CallerDevices(IReadOnlyList<CallerDevice> Devices, string? Message, int Total);