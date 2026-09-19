using Microsoft.Data.Sqlite;

namespace CallBridge.Service;

public sealed class CallBridgeStore
{
    private readonly string _connectionString;

    public CallBridgeStore(string filename)
    {
        var fullPath = Path.GetFullPath(filename);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS companies (id TEXT PRIMARY KEY,name TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS contacts (id TEXT PRIMARY KEY,company_id TEXT NOT NULL,name TEXT,source TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS phone_numbers (normalized TEXT NOT NULL,variant TEXT NOT NULL,company_id TEXT NOT NULL,contact_id TEXT,source TEXT NOT NULL,PRIMARY KEY(variant,company_id,contact_id));
            CREATE INDEX IF NOT EXISTS idx_phone_variant ON phone_numbers(variant);
            CREATE TABLE IF NOT EXISTS call_events (event_id TEXT PRIMARY KEY,provider TEXT NOT NULL,provider_call_id TEXT,direction TEXT,state TEXT,caller_number TEXT,called_number TEXT,extension TEXT,company_id TEXT,contact_id TEXT,occurred_at TEXT NOT NULL,received_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_call_events_received ON call_events(received_at DESC);
            CREATE TABLE IF NOT EXISTS call_notes (call_id TEXT PRIMARY KEY,notes TEXT NOT NULL DEFAULT '',outcome TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit_events (id INTEGER PRIMARY KEY AUTOINCREMENT,action TEXT NOT NULL,entity_type TEXT NOT NULL,entity_id TEXT NOT NULL,actor TEXT NOT NULL,occurred_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_audit_events_occurred ON audit_events(occurred_at DESC);
            """;
        command.ExecuteNonQuery();
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE contacts ADD COLUMN source TEXT NOT NULL DEFAULT ''";
        try { alter.ExecuteNonQuery(); } catch (SqliteException) { /* column already exists */ }
    }

    public IReadOnlyList<DirectoryRow> Directory(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 5000);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.name,ct.id,ct.name,p.normalized,COALESCE(p.source,ct.source)
            FROM contacts ct
            JOIN companies c ON c.id=ct.company_id
            LEFT JOIN phone_numbers p ON p.contact_id=ct.id
            GROUP BY c.id,ct.id,p.normalized
            ORDER BY c.name,ct.name,p.normalized
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<DirectoryRow>();
        while (reader.Read()) rows.Add(new(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        return rows;
    }

    public IReadOnlyList<DirectoryRow> ReplaceDirectory(IReadOnlyList<DirectoryRecordInput> records, string source)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM phone_numbers WHERE source=$source", ("$source", source));
        Execute(connection, transaction, "DELETE FROM contacts WHERE source=$source", ("$source", source));
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var record in records)
        {
            Execute(connection, transaction,
                "INSERT INTO companies(id,name,updated_at) VALUES($id,$name,$now) ON CONFLICT(id) DO UPDATE SET name=excluded.name,updated_at=excluded.updated_at",
                ("$id", record.CompanyId!), ("$name", record.CompanyName!), ("$now", now));
            if (!string.IsNullOrWhiteSpace(record.ContactId))
                Execute(connection, transaction,
                    "INSERT INTO contacts(id,company_id,name,source,updated_at) VALUES($id,$company,$name,$source,$now) ON CONFLICT(id) DO UPDATE SET company_id=excluded.company_id,name=excluded.name,source=excluded.source,updated_at=excluded.updated_at",
                    ("$id", record.ContactId!), ("$company", record.CompanyId!), ("$name", record.ContactName), ("$source", source), ("$now", now));
            foreach (var phone in record.Phones ?? []) InsertPhone(connection, transaction, phone, record.CompanyId!, record.ContactId, source);
        }
        AddAudit(connection, transaction, "replace", "directory", source, "desktop-session");
        transaction.Commit();
        return Directory(5000);
    }

    public int ClearImportedDirectory()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var deleted = Execute(connection, transaction, "DELETE FROM phone_numbers WHERE source='user-import'");
        AddAudit(connection, transaction, "clear", "directory", "user-import", "desktop-session");
        transaction.Commit();
        return deleted;
    }

    public IReadOnlyList<DirectoryRow> UpsertContact(ContactInput contact)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(connection, transaction,
            "INSERT INTO companies(id,name,updated_at) VALUES($id,$name,$now) ON CONFLICT(id) DO UPDATE SET name=excluded.name,updated_at=excluded.updated_at",
            ("$id", contact.CompanyId!), ("$name", contact.CompanyName!), ("$now", now));
        Execute(connection, transaction,
            "INSERT INTO contacts(id,company_id,name,source,updated_at) VALUES($id,$company,$name,'user-managed',$now) ON CONFLICT(id) DO UPDATE SET company_id=excluded.company_id,name=excluded.name,source=excluded.source,updated_at=excluded.updated_at",
            ("$id", contact.ContactId!), ("$company", contact.CompanyId!), ("$name", contact.ContactName), ("$now", now));
        Execute(connection, transaction, "DELETE FROM phone_numbers WHERE source IN ('user-managed','user-import') AND contact_id=$id", ("$id", contact.ContactId));
        foreach (var phone in contact.Phones ?? []) InsertPhone(connection, transaction, phone, contact.CompanyId!, contact.ContactId, "user-managed");
        AddAudit(connection, transaction, "upsert", "contact", contact.ContactId!, "desktop-session");
        transaction.Commit();
        return Directory(5000).Where(row => row.ContactId == contact.ContactId).ToArray();
    }

    public int DeleteContact(string contactId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var deleted = Execute(connection, transaction, "DELETE FROM phone_numbers WHERE source IN ('user-managed','user-import') AND contact_id=$id", ("$id", contactId));
        AddAudit(connection, transaction, "delete", "contact", contactId, "desktop-session");
        transaction.Commit();
        return deleted;
    }

    public IReadOnlyList<PhoneMatch> FindByPhone(string? phone)
    {
        var found = new Dictionary<string, PhoneMatch>(StringComparer.Ordinal);
        using var connection = Open();
        foreach (var variant in PhoneNumbers.Variants(phone))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.company_id,c.name,p.contact_id,ct.name,p.normalized
                FROM phone_numbers p
                JOIN companies c ON c.id=p.company_id
                LEFT JOIN contacts ct ON ct.id=p.contact_id
                WHERE p.variant=$variant;
                """;
            command.Parameters.AddWithValue("$variant", variant);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var match = new PhoneMatch(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
                found[$"{match.CompanyId}:{match.ContactId}"] = match;
            }
        }
        return [.. found.Values];
    }

    public StoredCallEvent SaveCallEvent(CallEventInput input)
    {
        var lookup = input.Direction == "inbound" ? input.CallerNumber : input.CalledNumber;
        var match = FindByPhone(lookup).FirstOrDefault();
        var occurredAt = input.OccurredAt ?? DateTimeOffset.UtcNow;
        var receivedAt = DateTimeOffset.UtcNow;
        var stored = new StoredCallEvent(input.EventId!, "standard-sip", input.CallId!, input.Direction!, input.State!, input.CallerNumber ?? "", input.CalledNumber ?? "", input.Extension ?? "", match?.CompanyId, match?.ContactId, occurredAt, receivedAt);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var inserted = Execute(connection, transaction, """
            INSERT OR IGNORE INTO call_events(event_id,provider,provider_call_id,direction,state,caller_number,called_number,extension,company_id,contact_id,occurred_at,received_at)
            VALUES($event,$provider,$call,$direction,$state,$caller,$called,$extension,$company,$contact,$occurred,$received)
            """,
            ("$event", stored.EventId), ("$provider", stored.Provider), ("$call", stored.ProviderCallId), ("$direction", stored.Direction),
            ("$state", stored.State), ("$caller", stored.CallerNumber), ("$called", stored.CalledNumber), ("$extension", stored.Extension),
            ("$company", stored.CompanyId), ("$contact", stored.ContactId), ("$occurred", occurredAt.ToString("O")), ("$received", receivedAt.ToString("O")));
        if (inserted == 0)
        {
            var existing = ReadEventById(connection, transaction, stored.EventId);
            if (!IsSameClientEvent(existing, stored)) throw new ApiValidationException("event_id_conflict");
            transaction.Commit();
            return existing;
        }
        transaction.Commit();
        return stored;
    }

    public IReadOnlyList<StoredCallEvent> RecentEvents(int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id,provider,COALESCE(provider_call_id,''),COALESCE(direction,''),COALESCE(state,''),COALESCE(caller_number,''),COALESCE(called_number,''),COALESCE(extension,''),company_id,contact_id,occurred_at,received_at
            FROM call_events ORDER BY received_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredCallEvent>();
        while (reader.Read()) rows.Add(ReadEvent(reader));
        return rows;
    }

    public IReadOnlyList<CallJournalRow> CallJournal(int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.provider,COALESCE(e.provider_call_id,e.event_id),COALESCE(e.direction,''),COALESCE(e.state,''),COALESCE(e.caller_number,''),COALESCE(e.called_number,''),COALESCE(e.extension,''),
                   COALESCE(e.company_id,''),COALESCE(c.name,''),COALESCE(e.contact_id,''),COALESCE(ct.name,''),e.occurred_at,
                   COALESCE(n.notes,''),COALESCE(n.outcome,''),n.updated_at
            FROM call_events e
            LEFT JOIN companies c ON c.id=e.company_id
            LEFT JOIN contacts ct ON ct.id=e.contact_id
            LEFT JOIN call_notes n ON n.call_id=COALESCE(e.provider_call_id,e.event_id)
            ORDER BY e.occurred_at DESC LIMIT $rowLimit;
            """;
        command.Parameters.AddWithValue("$rowLimit", Math.Min(limit * 20, 2000));
        using var reader = command.ExecuteReader();
        var calls = new Dictionary<string, MutableCall>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var callId = reader.GetString(1);
            var occurredAt = DateTimeOffset.Parse(reader.GetString(11));
            if (!calls.TryGetValue(callId, out var call))
            {
                call = new(reader.GetString(0), callId, reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), occurredAt, reader.GetString(12), reader.GetString(13), reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)));
                calls.Add(callId, call);
            }
            if (occurredAt < call.StartedAt) call.StartedAt = occurredAt;
            if (call.EndedAt is null && reader.GetString(3).ToLowerInvariant() is "hangup" or "ended" or "transfer" or "missed" or "failed" or "declined") call.EndedAt = occurredAt;
            if (call.CompanyId.Length == 0 && !reader.IsDBNull(7)) { call.CompanyId = reader.GetString(7); call.CompanyName = reader.GetString(8); }
            if (call.ContactId.Length == 0 && !reader.IsDBNull(9)) { call.ContactId = reader.GetString(9); call.ContactName = reader.GetString(10); }
        }
        return calls.Values.OrderByDescending(call => call.StartedAt).Take(limit).Select(call => call.ToRecord()).ToArray();
    }

    public CallNote? SaveCallNote(string callId, string? notes, string? outcome)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM call_events WHERE COALESCE(provider_call_id,event_id)=$id LIMIT 1";
        exists.Parameters.AddWithValue("$id", callId);
        if (exists.ExecuteScalar() is null) return null;
        var note = new CallNote(callId, (notes ?? "")[..Math.Min((notes ?? "").Length, 10000)], (outcome ?? "")[..Math.Min((outcome ?? "").Length, 100)], DateTimeOffset.UtcNow);
        Execute(connection, transaction, "INSERT INTO call_notes(call_id,notes,outcome,updated_at) VALUES($id,$notes,$outcome,$updated) ON CONFLICT(call_id) DO UPDATE SET notes=excluded.notes,outcome=excluded.outcome,updated_at=excluded.updated_at",
            ("$id", note.CallId), ("$notes", note.Notes), ("$outcome", note.Outcome), ("$updated", note.UpdatedAt.ToString("O")));
        AddAudit(connection, transaction, "upsert", "call-note", callId, "desktop-session");
        transaction.Commit();
        return note;
    }

    public int ApplyRetention(int days)
    {
        days = Math.Clamp(days, 1, 3650);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        var deleted = Execute(connection, transaction, "DELETE FROM call_events WHERE occurred_at < $cutoff", ("$cutoff", cutoff));
        Execute(connection, transaction, "DELETE FROM call_notes WHERE call_id NOT IN (SELECT COALESCE(provider_call_id,event_id) FROM call_events)");
        if (deleted > 0) AddAudit(connection, transaction, "retention-delete", "call-history", deleted.ToString(), "system");
        transaction.Commit();
        return deleted;
    }

    public int ClearCallHistory()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM call_notes");
        var deleted = Execute(connection, transaction, "DELETE FROM call_events");
        AddAudit(connection, transaction, "clear", "call-history", deleted.ToString(), "desktop-session");
        transaction.Commit();
        return deleted;
    }

    public IReadOnlyList<AuditEvent> RecentAudit(int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,action,entity_type,entity_id,actor,occurred_at FROM audit_events ORDER BY occurred_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<AuditEvent>();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        return rows;
    }

    public object Diagnostics(int retentionDays)
    {
        using var connection = Open();
        long Count(string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(command.ExecuteScalar() ?? 0L);
        }
        return new { companies = Count("companies"), contacts = Count("contacts"), phones = Count("phone_numbers"), calls = Count("call_events"), auditEvents = Count("audit_events"), retentionDays };
    }

    private static StoredCallEvent ReadEvent(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10)), DateTimeOffset.Parse(reader.GetString(11)));

    private static StoredCallEvent ReadEventById(SqliteConnection connection, SqliteTransaction transaction, string eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id,provider,COALESCE(provider_call_id,''),COALESCE(direction,''),COALESCE(state,''),COALESCE(caller_number,''),COALESCE(called_number,''),COALESCE(extension,''),company_id,contact_id,occurred_at,received_at
            FROM call_events WHERE event_id=$event LIMIT 1;
            """;
        command.Parameters.AddWithValue("$event", eventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The stored call event could not be read after an idempotent insert.");
        return ReadEvent(reader);
    }

    private static bool IsSameClientEvent(StoredCallEvent left, StoredCallEvent right) =>
        left.ProviderCallId == right.ProviderCallId
        && left.Direction == right.Direction
        && left.State == right.State
        && left.CallerNumber == right.CallerNumber
        && left.CalledNumber == right.CalledNumber
        && left.Extension == right.Extension
        && left.OccurredAt.ToUniversalTime() == right.OccurredAt.ToUniversalTime();

    private static void InsertPhone(SqliteConnection connection, SqliteTransaction transaction, string phone, string companyId, string? contactId, string source)
    {
        var normalized = PhoneNumbers.Normalize(phone);
        foreach (var variant in PhoneNumbers.Variants(phone))
            Execute(connection, transaction, "INSERT OR REPLACE INTO phone_numbers(normalized,variant,company_id,contact_id,source) VALUES($normalized,$variant,$company,$contact,$source)",
                ("$normalized", normalized), ("$variant", variant), ("$company", companyId), ("$contact", contactId), ("$source", source));
    }

    private static void AddAudit(SqliteConnection connection, SqliteTransaction transaction, string action, string entityType, string entityId, string actor) =>
        Execute(connection, transaction, "INSERT INTO audit_events(action,entity_type,entity_id,actor,occurred_at) VALUES($action,$type,$entity,$actor,$at)",
            ("$action", action), ("$type", entityType), ("$entity", entityId), ("$actor", actor), ("$at", DateTimeOffset.UtcNow.ToString("O")));

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private sealed class MutableCall(string provider, string callId, string direction, string state, string callerNumber, string calledNumber, string extension, string companyId, string companyName, string contactId, string contactName, DateTimeOffset startedAt, string notes, string outcome, DateTimeOffset? notesUpdatedAt)
    {
        public string Provider { get; } = provider;
        public string CallId { get; } = callId;
        public string Direction { get; } = direction;
        public string State { get; } = state;
        public string CallerNumber { get; } = callerNumber;
        public string CalledNumber { get; } = calledNumber;
        public string Extension { get; } = extension;
        public string CompanyId { get; set; } = companyId;
        public string CompanyName { get; set; } = companyName;
        public string ContactId { get; set; } = contactId;
        public string ContactName { get; set; } = contactName;
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public DateTimeOffset? EndedAt { get; set; }
        public string Notes { get; } = notes;
        public string Outcome { get; } = outcome;
        public DateTimeOffset? NotesUpdatedAt { get; } = notesUpdatedAt;
        public CallJournalRow ToRecord() => new(CallId, Provider, Direction, State, CallerNumber, CalledNumber, Extension, CompanyId, CompanyName, ContactId, ContactName, StartedAt, EndedAt, EndedAt is null ? 0 : Math.Max(0, (long)Math.Round((EndedAt.Value - StartedAt).TotalSeconds)), Notes, Outcome, NotesUpdatedAt);
    }
}
