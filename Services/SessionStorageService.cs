using System.Collections.Generic;
using System.Text.Json.Serialization;
using CxShell.Models;

namespace CxShell.Services;

public class SessionData
{
    public string Format { get; set; } = "CxShell.Session";
    public string Version { get; set; } = "1.0";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApplicationSettings? Settings { get; set; } = new();
    public List<SessionGroup> Groups { get; set; } = new();
    public List<SessionInfo> Sessions { get; set; } = new();
    public List<Guid> QuickSessionIds { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExportedAt { get; set; }
}

public class SessionStorageService
{
    private readonly string _storageDir;
    private readonly string _storagePath;
    private readonly SqliteAppDataStore _store;

    public SessionStorageService(string? directory = null)
    {
        _storageDir = string.IsNullOrWhiteSpace(directory)
            ? GetStorageDirectory()
            : Path.GetFullPath(directory);
        _storagePath = Path.Combine(_storageDir, "sessions.json");
        _store = new SqliteAppDataStore(_storageDir);
    }

    public static string GetStorageDirectory()
    {
        return AppDataPaths.GetStorageDirectory();
    }

    public SessionData Load()
    {
        var payload = _store.Read("sessions");
        if (payload == null)
        {
            _store.ImportLegacyFile(
                "sessions",
                "default",
                _storagePath,
                static json => TryDeserialize(json) != null);
            payload = _store.Read("sessions");
        }

        return string.IsNullOrWhiteSpace(payload)
            ? new SessionData()
            : TryDeserialize(payload) ?? new SessionData();
    }

    public void Save(SessionData data)
    {
        var persisted = new SessionData
        {
            Format = data.Format,
            Version = data.Version,
            // Settings are stored separately in SQLite; keep loading this field
            // only to migrate values embedded in older session files.
            Settings = null,
            Groups = data.Groups,
            Sessions = data.Sessions,
            QuickSessionIds = data.QuickSessionIds,
            ExportedAt = data.ExportedAt
        };
        var json = System.Text.Json.JsonSerializer.Serialize(persisted, AppJsonOptions.Default);
        _store.Write("sessions", "default", json);
    }

    private static SessionData? TryDeserialize(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SessionData>(json, AppJsonOptions.Default);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
