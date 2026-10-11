using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NebulaAuth.NebulaLink.Protocol;
using Newtonsoft.Json;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Content of nebula.json: non-secret metadata of the Achiefy link and the local account links with their grants.
///     No credential, no password, no code is ever stored here (accounts are keyed by normalized login).
/// </summary>
public sealed class NebulaLinkFile
{
    public const int CURRENT_VERSION = 1;

    [JsonProperty("version")] public int Version { get; set; } = CURRENT_VERSION;
    [JsonProperty("paused")] public bool Paused { get; set; }
    [JsonProperty("tenantBaseUrl")] public string? TenantBaseUrl { get; set; }
    [JsonProperty("linkId")] public Guid? LinkId { get; set; }
    [JsonProperty("tenantId")] public int? TenantId { get; set; }
    [JsonProperty("tenantLabel")] public string? TenantLabel { get; set; }
    [JsonProperty("accountLabel")] public string? AccountLabel { get; set; }
    [JsonProperty("deviceName")] public string? DeviceName { get; set; }
    [JsonProperty("pairedAt")] public DateTimeOffset? PairedAt { get; set; }

    /// <summary>"encrypted" | "os": where the device credential of <see cref="LinkId" /> is kept.</summary>
    [JsonProperty("credentialStore")] public string? CredentialStore { get; set; }

    /// <summary>Last Hello refused with incompatible_version: the server major (retry every 6 h).</summary>
    [JsonProperty("incompatibleServerMajor")] public int? IncompatibleServerMajor { get; set; }

    [JsonProperty("incompatibleAt")] public DateTimeOffset? IncompatibleAt { get; set; }

    [JsonProperty("accounts")] public List<NebulaAccountLink> Accounts { get; set; } = [];

    /// <summary>The last module catalog of the server (sanitized): permissions can be shown while offline.</summary>
    [JsonProperty("moduleCatalog")] public NebulaModuleCatalog? ModuleCatalog { get; set; }

    /// <summary>Duplicate mafiles the user resolved ("Use this one"), per login of this link.</summary>
    [JsonProperty("duplicateChoices")] public List<DuplicateChoice> DuplicateChoices { get; set; } = [];

    public NebulaLinkFile Clone()
    {
        var copy = (NebulaLinkFile) MemberwiseClone();
        copy.Accounts = Accounts.Select(a => a.Clone()).ToList();
        copy.DuplicateChoices = DuplicateChoices.Select(c => c.Clone()).ToList();
        return copy;
    }
}

/// <summary>
///     The mafile the user picked among local duplicates of one login (by file name; files are never renamed or
///     deleted). <see cref="Candidates" /> are the file names offered at that moment: a new file with another secret
///     raises the conflict again.
/// </summary>
public sealed class DuplicateChoice
{
    [JsonProperty("login")] public string Login { get; set; } = "";
    [JsonProperty("fileName")] public string FileName { get; set; } = "";
    [JsonProperty("candidates")] public List<string> Candidates { get; set; } = [];

    public DuplicateChoice Clone()
    {
        var copy = (DuplicateChoice) MemberwiseClone();
        copy.Candidates = Candidates.ToList();
        return copy;
    }
}

public sealed class NebulaAccountLink
{
    /// <summary>Normalized login (<see cref="AccountMatcher.Normalize" />) of the local mafile.</summary>
    [JsonProperty("login")] public string Login { get; set; } = "";

    [JsonProperty("accountId")] public int AccountId { get; set; }

    /// <summary>
    ///     Optional permissions that are ON, by (category, module id); login codes and confirmation checks need none.
    /// </summary>
    [JsonProperty("moduleGrants")] public List<GrantKey> Grants { get; set; } = [];

    [JsonProperty("linkedAt")] public DateTimeOffset LinkedAt { get; set; }

    public NebulaAccountLink Clone()
    {
        var copy = (NebulaAccountLink) MemberwiseClone();
        copy.Grants = Grants.ToList();
        return copy;
    }

    public bool HasGrant(GrantKey key)
    {
        return Grants.Contains(key);
    }
}

/// <summary>
///     nebula.json in the data folder (atomic writes). Exists only while the device is paired: written by a successful
///     pairing, deleted by unlink / revoke. A new pairing (new link id) clears the account links, so grants never carry
///     over to another link.
/// </summary>
public sealed class LinkStore
{
    public const string FILE_NAME = "nebula.json";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.DateTimeOffset,
        Formatting = Formatting.Indented
    };

    private readonly object _lock = new();
    private NebulaLinkFile? _data;
    private Dictionary<string, NebulaAccountLink> _index = new(StringComparer.Ordinal);

    /// <param name="path">File path; null keeps everything in memory (previews, tests).</param>
    public LinkStore(string? path)
    {
        FilePath = path;
    }

    public string? FilePath { get; }

    /// <summary>Raised after every change (on the thread that changed it).</summary>
    public event Action? Changed;

    /// <summary>Reads the file. A damaged file is moved aside and treated as absent (the user pairs again).</summary>
    public void Load()
    {
        lock (_lock)
        {
            SetData(null);
            if (FilePath == null || !File.Exists(FilePath)) return;
            try
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var data = JsonConvert.DeserializeObject<NebulaLinkFile>(json, JsonSettings);
                if (data == null || data.Version > NebulaLinkFile.CURRENT_VERSION)
                {
                    NebulaLinkLog.Warn($"{FILE_NAME} has an unsupported version; the integration stays off");
                    return;
                }

                data.Accounts ??= [];
                data.Accounts.RemoveAll(a => string.IsNullOrWhiteSpace(a.Login));
                data.DuplicateChoices ??= [];
                data.DuplicateChoices.RemoveAll(c => string.IsNullOrWhiteSpace(c.Login) || string.IsNullOrWhiteSpace(c.FileName));
                if (data.ModuleCatalog != null) data.ModuleCatalog = ModuleCatalogs.Sanitize(data.ModuleCatalog);
                foreach (var account in data.Accounts)
                {
                    account.Login = AccountMatcher.Normalize(account.Login);
                    account.Grants = (account.Grants ?? [])
                        .Where(g => g != null && ModuleCatalogs.IsKnownCategory(g.Category) && ModuleCatalogs.IsValidId(g.ModuleId))
                        .Distinct().Take(NebulaCatalogLimits.MaxModules).ToList();
                }

                SetData(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                NebulaLinkLog.Error($"{FILE_NAME} can't be read; the integration stays off", ex);
                try
                {
                    if (ex is JsonException)
                        File.Move(FilePath, FilePath + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}");
                }
                catch
                {
                    // Left in place; read again on the next start
                }
            }
        }
    }

    /// <summary>A copy of the current content (null when the file does not exist).</summary>
    public NebulaLinkFile? Snapshot()
    {
        lock (_lock) return _data?.Clone();
    }

    /// <summary>Changes the content (created when absent) and publishes the file.</summary>
    public void Update(Action<NebulaLinkFile> change)
    {
        lock (_lock)
        {
            var data = _data?.Clone() ?? new NebulaLinkFile();
            change(data);
            Save(data);
            SetData(data);
        }

        Changed?.Invoke();
    }

    /// <summary>Deletes nebula.json (the link is gone: unlink, revoke).</summary>
    public void Delete()
    {
        lock (_lock)
        {
            SetData(null);
            if (FilePath != null) NebulaLinkFiles.TryDelete(FilePath);
        }

        Changed?.Invoke();
    }

    public NebulaAccountLink? FindByLogin(string login)
    {
        lock (_lock) return _index.TryGetValue(login, out var link) ? link.Clone() : null;
    }

    public NebulaAccountLink? FindByAccountId(int accountId)
    {
        lock (_lock) return _data?.Accounts.FirstOrDefault(a => a.AccountId == accountId)?.Clone();
    }

    public IReadOnlyList<NebulaAccountLink> Accounts()
    {
        lock (_lock) return _data?.Accounts.Select(a => a.Clone()).ToList() ?? [];
    }

    /// <summary>
    ///     Records a new link id. The account links (and their grants) of any previous link are cleared.
    /// </summary>
    public void SetLink(Guid linkId, Action<NebulaLinkFile> fill)
    {
        Update(d =>
        {
            if (d.LinkId != linkId)
            {
                d.Accounts.Clear();
                d.ModuleCatalog = null;
                d.DuplicateChoices.Clear();
            }
            d.LinkId = linkId;
            fill(d);
        });
    }

    /// <summary>The account of this normalized login is linked under the current link (fast, no copy).</summary>
    public bool IsLinked(string login)
    {
        lock (_lock) return _data?.LinkId != null && _index.ContainsKey(login);
    }

    /// <summary>A link to Achiefy exists (paired).</summary>
    public bool HasLink
    {
        get
        {
            lock (_lock) return _data?.LinkId != null;
        }
    }

    // Under the lock
    private void SetData(NebulaLinkFile? data)
    {
        _data = data;
        _index = data?.Accounts.GroupBy(a => a.Login).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal) ??
                 new Dictionary<string, NebulaAccountLink>(StringComparer.Ordinal);
    }

    private void Save(NebulaLinkFile data)
    {
        if (FilePath == null) return;
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data, JsonSettings));
        NebulaLinkFiles.Write(FilePath, bytes);
    }
}
