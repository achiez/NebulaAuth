using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NebulaAuth.NebulaLink.Protocol;
using Newtonsoft.Json;

namespace NebulaAuth.NebulaLink;

/// <summary>One Achiefy request and its result (no codes, no secrets, no confirmation content).</summary>
public sealed class RequestRecord
{
    [JsonProperty("requestId")] public Guid RequestId { get; set; }
    [JsonProperty("time")] public DateTimeOffset Time { get; set; }
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("login")] public string? Login { get; set; }
    [JsonProperty("module")] public string Module { get; set; } = "";
    [JsonProperty("category")] public string? Category { get; set; }
    [JsonProperty("type")] public string Type { get; set; } = "";
    [JsonProperty("operationType")] public string? OperationType { get; set; }
    [JsonProperty("idCount")] public int IdCount { get; set; }

    /// <summary>The operation ids (CreatorIds) of the request; null without an operation.</summary>
    [JsonProperty("ids")] public string[]? Ids { get; set; }
    [JsonProperty("decision")] public string? Decision { get; set; }
    [JsonProperty("result")] public string Result { get; set; } = "";
}

/// <summary>A confirmation accepted/declined through NebulaAuth (no nonce, no content).</summary>
public sealed class DecisionRecord
{
    public const string ORIGIN_MANUAL = "manual";
    public const string ORIGIN_ACHIEFY = "achiefy";

    [JsonProperty("login")] public string Login { get; set; } = "";
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("creatorId")] public string CreatorId { get; set; } = "";
    [JsonProperty("decision")] public string Decision { get; set; } = "";
    [JsonProperty("origin")] public string Origin { get; set; } = "";
    [JsonProperty("requestId")] public Guid? RequestId { get; set; }
    [JsonProperty("time")] public DateTimeOffset Time { get; set; }

    [JsonIgnore] public bool Accepted => Decision == NebulaDecisions.Accept;

    /// <summary>The protocol resolution string of this decision.</summary>
    [JsonIgnore]
    public string Resolution => (Origin, Accepted) switch
    {
        (ORIGIN_MANUAL, true) => NebulaResolutions.ConfirmedByUser,
        (ORIGIN_MANUAL, false) => NebulaResolutions.DeclinedByUser,
        (_, true) => NebulaResolutions.ConfirmedByAchiefy,
        _ => NebulaResolutions.DeclinedByAchiefy
    };
}

/// <summary>Operation referenced by an Achiefy command in the last 24 h (manual decisions on it are reported).</summary>
public sealed class KnownOperationRecord
{
    [JsonProperty("login")] public string Login { get; set; } = "";
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("creatorId")] public string CreatorId { get; set; } = "";
    [JsonProperty("accountId")] public int AccountId { get; set; }
    [JsonProperty("lastRequestAt")] public DateTimeOffset LastRequestAt { get; set; }
}

/// <summary>A send whose outcome is unknown (exception after the request was written).</summary>
public sealed class UncertainRecord
{
    [JsonProperty("login")] public string Login { get; set; } = "";
    [JsonProperty("type")] public int Type { get; set; }
    [JsonProperty("creatorId")] public string CreatorId { get; set; } = "";
    [JsonProperty("sentAt")] public DateTimeOffset SentAt { get; set; }
}

public sealed class PendingEventRecord
{
    [JsonProperty("queuedAt")] public DateTimeOffset QueuedAt { get; set; }
    [JsonProperty("event")] public UserActionEvent Event { get; set; } = new();
}

public sealed class NebulaLinkHistoryFile
{
    [JsonProperty("version")] public int Version { get; set; } = 1;
    [JsonProperty("requests")] public List<RequestRecord> Requests { get; set; } = [];
    [JsonProperty("decisions")] public List<DecisionRecord> Decisions { get; set; } = [];
    [JsonProperty("knownOperations")] public List<KnownOperationRecord> KnownOperations { get; set; } = [];
    [JsonProperty("pendingEvents")] public List<PendingEventRecord> PendingEvents { get; set; } = [];
    [JsonProperty("uncertain")] public List<UncertainRecord> Uncertain { get; set; } = [];
}

/// <summary>
///     nebula-history.json: bounded local history kept across restarts (requests 1000 / 30 d, decisions 2000 / 7 d, known
///     operations 24 h, pending user-action events 200 / 24 h, uncertain sends 24 h). Writes are debounced (2 s). The file
///     is written only while <see cref="Persist" /> is on (the device is paired).
/// </summary>
public sealed class NebulaLinkHistoryStore : IDisposable
{
    public const string FILE_NAME = "nebula-history.json";
    public const int MAX_REQUESTS = 1000;
    public const int MAX_DECISIONS = 2000;
    public const int MAX_EVENTS = 200;
    public static readonly TimeSpan RequestsAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan DecisionsAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan KnownAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan EventsAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan UncertainAge = TimeSpan.FromHours(24);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        DateParseHandling = DateParseHandling.DateTimeOffset,
        Formatting = Formatting.None
    };

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _saveTimer;
    private NebulaLinkHistoryFile _data = new();
    private bool _dirty;
    private bool _disposed;

    public NebulaLinkHistoryStore(string? path, Func<DateTimeOffset>? clock = null)
    {
        FilePath = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _saveTimer = new Timer(_ => Flush());
    }

    public string? FilePath { get; }

    /// <summary>Changes are written to disk (the integration is enabled). Off: memory only.</summary>
    public bool Persist { get; set; }

    public event Action? Changed;

    public void Load()
    {
        lock (_lock)
        {
            _data = new NebulaLinkHistoryFile();
            if (FilePath == null || !File.Exists(FilePath)) return;
            try
            {
                _data = JsonConvert.DeserializeObject<NebulaLinkHistoryFile>(File.ReadAllText(FilePath, Encoding.UTF8),
                    JsonSettings) ?? new NebulaLinkHistoryFile();
                _data.Requests ??= [];
                _data.Decisions ??= [];
                _data.KnownOperations ??= [];
                _data.PendingEvents ??= [];
                _data.Uncertain ??= [];
                Prune();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                NebulaLinkLog.Warn($"{FILE_NAME} can't be read; starting with an empty history", ex);
                _data = new NebulaLinkHistoryFile();
            }
        }
    }

    #region Requests

    public void AddRequest(RequestRecord record)
    {
        Mutate(d =>
        {
            d.Requests.RemoveAll(r => r.RequestId == record.RequestId);
            d.Requests.Add(record);
        });
    }

    public IReadOnlyList<RequestRecord> Requests()
    {
        lock (_lock) return _data.Requests.OrderByDescending(r => r.Time).ToList();
    }

    /// <summary>Last request per Achiefy account id.</summary>
    public IReadOnlyDictionary<int, RequestRecord> LastRequestByAccount()
    {
        lock (_lock)
        {
            var result = new Dictionary<int, RequestRecord>();
            foreach (var r in _data.Requests)
            {
                if (!result.TryGetValue(r.AccountId, out var existing) || existing.Time < r.Time)
                    result[r.AccountId] = r;
            }

            return result;
        }
    }

    #endregion

    #region Decisions

    public void AddDecision(DecisionRecord record)
    {
        Mutate(d =>
        {
            d.Decisions.RemoveAll(x =>
                x.Login == record.Login && x.Type == record.Type && x.CreatorId == record.CreatorId);
            d.Decisions.Add(record);
            // A clean decision ends the uncertainty of an earlier send
            d.Uncertain.RemoveAll(x =>
                x.Login == record.Login && x.Type == record.Type && x.CreatorId == record.CreatorId);
        });
    }

    public DecisionRecord? FindDecision(string login, int type, string creatorId)
    {
        lock (_lock)
        {
            var cutoff = _clock() - DecisionsAge;
            return _data.Decisions.LastOrDefault(x =>
                x.Login == login && x.Type == type && x.CreatorId == creatorId && x.Time >= cutoff);
        }
    }

    public IReadOnlyList<DecisionRecord> Decisions()
    {
        lock (_lock) return _data.Decisions.OrderByDescending(d => d.Time).ToList();
    }

    #endregion

    #region Known operations

    public void TouchKnownOperation(string login, int accountId, int type, string creatorId)
    {
        Mutate(d =>
        {
            var existing = d.KnownOperations.FirstOrDefault(x =>
                x.Login == login && x.Type == type && x.CreatorId == creatorId);
            if (existing == null)
            {
                d.KnownOperations.Add(new KnownOperationRecord
                {
                    Login = login, AccountId = accountId, Type = type, CreatorId = creatorId,
                    LastRequestAt = _clock()
                });
            }
            else
            {
                existing.AccountId = accountId;
                existing.LastRequestAt = _clock();
            }
        });
    }

    public KnownOperationRecord? FindKnownOperation(string login, int type, string creatorId)
    {
        lock (_lock)
        {
            var cutoff = _clock() - KnownAge;
            return _data.KnownOperations.FirstOrDefault(x =>
                x.Login == login && x.Type == type && x.CreatorId == creatorId && x.LastRequestAt >= cutoff);
        }
    }

    #endregion

    #region Uncertain

    public void AddUncertain(string login, int type, string creatorId)
    {
        Mutate(d =>
        {
            d.Uncertain.RemoveAll(x => x.Login == login && x.Type == type && x.CreatorId == creatorId);
            d.Uncertain.Add(new UncertainRecord {Login = login, Type = type, CreatorId = creatorId, SentAt = _clock()});
        });
    }

    public UncertainRecord? FindUncertain(string login, int type, string creatorId)
    {
        lock (_lock)
        {
            var cutoff = _clock() - UncertainAge;
            return _data.Uncertain.FirstOrDefault(x =>
                x.Login == login && x.Type == type && x.CreatorId == creatorId && x.SentAt >= cutoff);
        }
    }

    #endregion

    #region Pending events

    public void QueueEvent(UserActionEvent evt)
    {
        Mutate(d =>
        {
            d.PendingEvents.RemoveAll(x => x.Event.EventId == evt.EventId);
            d.PendingEvents.Add(new PendingEventRecord {QueuedAt = _clock(), Event = evt});
        });
    }

    public IReadOnlyList<UserActionEvent> PendingEvents()
    {
        lock (_lock)
        {
            var cutoff = _clock() - EventsAge;
            return _data.PendingEvents.Where(x => x.QueuedAt >= cutoff).Select(x => x.Event).ToList();
        }
    }

    public void RemoveEvent(Guid eventId)
    {
        Mutate(d => d.PendingEvents.RemoveAll(x => x.Event.EventId == eventId));
    }

    #endregion

    /// <summary>Forgets everything that belongs to a link (events that can't be delivered any more).</summary>
    /// <summary>
    ///     The link is gone or replaced: what belongs to it (queued user action events, the operations its requests
    ///     referenced, uncertain sends) is cleared; the request history and the decisions stay.
    /// </summary>
    public void ClearLinkData()
    {
        Mutate(d =>
        {
            d.PendingEvents.Clear();
            d.KnownOperations.Clear();
            d.Uncertain.Clear();
        });
    }

    private void Mutate(Action<NebulaLinkHistoryFile> change)
    {
        lock (_lock)
        {
            change(_data);
            Prune();
            _dirty = true;
            if (Persist && FilePath != null && !_disposed) _saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke();
    }

    private void Prune()
    {
        var now = _clock();
        _data.Requests.RemoveAll(r => r.Time < now - RequestsAge);
        if (_data.Requests.Count > MAX_REQUESTS)
            _data.Requests = _data.Requests.OrderBy(r => r.Time).Skip(_data.Requests.Count - MAX_REQUESTS).ToList();
        _data.Decisions.RemoveAll(r => r.Time < now - DecisionsAge);
        if (_data.Decisions.Count > MAX_DECISIONS)
            _data.Decisions = _data.Decisions.OrderBy(r => r.Time).Skip(_data.Decisions.Count - MAX_DECISIONS).ToList();
        _data.KnownOperations.RemoveAll(r => r.LastRequestAt < now - KnownAge);
        _data.PendingEvents.RemoveAll(r => r.QueuedAt < now - EventsAge);
        if (_data.PendingEvents.Count > MAX_EVENTS)
            _data.PendingEvents = _data.PendingEvents.OrderBy(r => r.QueuedAt)
                .Skip(_data.PendingEvents.Count - MAX_EVENTS).ToList();
        _data.Uncertain.RemoveAll(r => r.SentAt < now - UncertainAge);
    }

    /// <summary>Writes pending changes now (app exit, tests).</summary>
    public void Flush()
    {
        byte[] bytes;
        lock (_lock)
        {
            if (!_dirty || !Persist || FilePath == null) return;
            bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_data, JsonSettings));
            _dirty = false;
        }

        try
        {
            NebulaLinkFiles.Write(FilePath, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NebulaLinkLog.Warn($"{FILE_NAME} can't be written now", ex);
            lock (_lock) _dirty = true;
        }
    }

    public void Dispose()
    {
        Flush();
        lock (_lock) _disposed = true;
        _saveTimer.Dispose();
    }
}
