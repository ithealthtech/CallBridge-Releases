using System.IO;
using System.Text.Json;

namespace CallBridge.Desktop;

public sealed class ProtectedCallEventQueue
{
    private readonly string _path;
    private readonly int _maximumEvents;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<SipCallLifecycleEvent>? _events;

    public ProtectedCallEventQueue(string path, int maximumEvents = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEvents, 1);
        _path = Path.GetFullPath(path);
        _maximumEvents = maximumEvents;
    }

    public async Task EnqueueAndDrainAsync(
        SipCallLifecycleEvent callEvent,
        Func<SipCallLifecycleEvent, Task<bool>> sender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callEvent);
        ArgumentNullException.ThrowIfNull(sender);
        Validate(callEvent);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var events = _events!;
            if (!events.Any(item => string.Equals(item.EventId, callEvent.EventId, StringComparison.Ordinal)))
            {
                events.Add(callEvent);
                if (events.Count > _maximumEvents)
                    events.RemoveRange(0, events.Count - _maximumEvents);
                Persist();
            }

            await DrainCoreAsync(sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DrainAsync(
        Func<SipCallLifecycleEvent, Task<bool>> sender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            await DrainCoreAsync(sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return _events!.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DrainCoreAsync(
        Func<SipCallLifecycleEvent, Task<bool>> sender,
        CancellationToken cancellationToken)
    {
        while (_events!.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool delivered;
            try
            {
                delivered = await sender(_events[0]).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                delivered = false;
            }

            if (!delivered) return;
            _events.RemoveAt(0);
            Persist();
        }
    }

    private void EnsureLoaded()
    {
        if (_events is not null) return;
        _events = [];
        if (!File.Exists(_path)) return;

        try
        {
            var protectedJson = File.ReadAllText(_path);
            var json = CredentialProtector.Unprotect(protectedJson);
            var loaded = JsonSerializer.Deserialize<List<SipCallLifecycleEvent>>(json) ?? [];
            foreach (var callEvent in loaded)
            {
                Validate(callEvent);
                if (_events.Any(item => string.Equals(item.EventId, callEvent.EventId, StringComparison.Ordinal))) continue;
                _events.Add(callEvent);
            }

            if (_events.Count > _maximumEvents)
                _events.RemoveRange(0, _events.Count - _maximumEvents);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or FormatException
            or System.ComponentModel.Win32Exception
            or System.Security.Cryptography.CryptographicException
            or JsonException
            or InvalidDataException)
        {
            QuarantineCorruptFile();
            _events.Clear();
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (_events!.Count == 0)
        {
            File.Delete(_path);
            return;
        }

        var json = JsonSerializer.Serialize(_events);
        var protectedJson = CredentialProtector.Protect(json);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, protectedJson);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void QuarantineCorruptFile()
    {
        var quarantinePath = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_path, quarantinePath);
        }
        catch (IOException)
        {
            // Preserve the original file when another process has it open.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original file when its ACL prevents quarantine.
        }
    }

    private static void Validate(SipCallLifecycleEvent callEvent)
    {
        if (string.IsNullOrWhiteSpace(callEvent.EventId)
            || string.IsNullOrWhiteSpace(callEvent.CallId)
            || callEvent.EventId.Length > 200
            || callEvent.CallId.Length > 200
            || callEvent.RemoteNumber.Length > 64
            || callEvent.Direction == SipCallDirection.None
            || !Enum.IsDefined(callEvent.Direction)
            || !Enum.IsDefined(callEvent.State))
            throw new InvalidDataException("The call lifecycle event is incomplete.");
    }
}
