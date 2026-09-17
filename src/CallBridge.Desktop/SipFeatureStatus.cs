using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace CallBridge.Desktop;

/// <summary>Voicemail counts from a SIP message-summary notification (RFC 3842).</summary>
public sealed record VoicemailStatus(bool MessagesWaiting, int NewMessages, int SavedMessages, bool Known)
{
    public static VoicemailStatus Unknown { get; } = new(false, 0, 0, false);

    public string Summary => !Known
        ? "Waiting for voicemail status from the phone system"
        : NewMessages > 0
            ? $"{NewMessages} new · {SavedMessages} saved"
            : MessagesWaiting ? "New messages waiting" : SavedMessages > 0 ? $"No new messages · {SavedMessages} saved" : "No new messages";

    /// <summary>
    /// Parses a message-summary body such as "Messages-Waiting: yes" and "Voice-Message: 2/5 (0/0)".
    /// Returns null when the body isn't a message summary.
    /// </summary>
    public static VoicemailStatus? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        bool? waiting = null;
        int newMessages = 0, savedMessages = 0;
        var sawVoice = false;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Messages-Waiting", StringComparison.OrdinalIgnoreCase))
            {
                waiting = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
            else if (name.Equals("Voice-Message", StringComparison.OrdinalIgnoreCase))
            {
                var counts = value.Split(' ', 2)[0].Split('/');
                if (counts.Length == 2 && int.TryParse(counts[0], out var fresh) && int.TryParse(counts[1], out var old))
                {
                    newMessages = Math.Clamp(fresh, 0, 9999);
                    savedMessages = Math.Clamp(old, 0, 9999);
                    sawVoice = true;
                }
            }
        }
        if (waiting is null && !sawVoice) return null;
        return new VoicemailStatus(waiting ?? newMessages > 0, newMessages, savedMessages, true);
    }
}

public enum ParkSlotState
{
    Unknown,
    Open,
    Occupied
}

public sealed record ParkSlotStatus(string Slot, ParkSlotState State)
{
    public string Summary => State switch
    {
        ParkSlotState.Occupied => "A call is parked here",
        ParkSlotState.Open => "Open",
        _ => "Status unavailable from the phone system"
    };

    /// <summary>
    /// Reads a dialog-info notification (RFC 4235) for a park slot. Any early or confirmed dialog means a call is parked.
    /// </summary>
    public static ParkSlotState ParseDialogInfo(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ParkSlotState.Unknown;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256_000 };
            using var reader = XmlReader.Create(new StringReader(body), settings);
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "dialog-info") return ParkSlotState.Unknown;
            var busy = document.Root.Elements()
                .Where(element => element.Name.LocalName == "dialog")
                .Select(dialog => dialog.Elements().FirstOrDefault(element => element.Name.LocalName == "state")?.Value.Trim())
                .Any(state => state is not null && (state.Equals("confirmed", StringComparison.OrdinalIgnoreCase) || state.Equals("early", StringComparison.OrdinalIgnoreCase)));
            return busy ? ParkSlotState.Occupied : ParkSlotState.Open;
        }
        catch (XmlException)
        {
            return ParkSlotState.Unknown;
        }
    }
}

/// <summary>Dial-string rules for voicemail and park codes entered by an admin.</summary>
public static class SipFeatureCodes
{
    public static bool IsValidDialString(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 32 && value.Trim().All(ch => char.IsAsciiDigit(ch) || ch is '*' or '#' or '+');

    /// <summary>Splits "701, 702-704" into individual slot numbers. Returns false with a message when the list is invalid.</summary>
    public static bool TryParseSlots(string? value, out List<string> slots, out string message)
    {
        slots = [];
        message = "";
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var part in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var first) && int.TryParse(range[1], out var last) && first > 0 && last >= first && last - first < 50)
            {
                for (var slot = first; slot <= last; slot++) slots.Add(slot.ToString());
            }
            else if (IsValidDialString(part))
            {
                slots.Add(part.Trim());
            }
            else
            {
                message = $"\"{part}\" isn't a valid park slot. Use numbers like 701, 702 or a range like 701-705.";
                slots = [];
                return false;
            }
        }
        slots = slots.Distinct(StringComparer.Ordinal).ToList();
        if (slots.Count > 20)
        {
            message = "Configure 20 park slots or fewer.";
            slots = [];
            return false;
        }
        return true;
    }
}
