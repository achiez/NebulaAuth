using System;
using System.IO;
using NebulaAuth.Core;
using Newtonsoft.Json;

namespace NebulaAuth.Model.Update;

public class UpdateSettings
{
    private static string FilePath => Path.Combine(DataRoot.Combine("settings"), "update.json");

    [JsonProperty("skippedVersion")] public string? SkippedVersion { get; set; }

    [JsonProperty("remindAfter")] public DateTime? RemindAfter { get; set; }

    /// <summary>
    ///     "beta" follows the beta feed of this OS/architecture (switching back to "stable" may install an older stable
    ///     version). Null = the channel the installed package was built for.
    /// </summary>
    [JsonProperty("channel", NullValueHandling = NullValueHandling.Ignore)]
    public string? Channel { get; set; }

    /// <summary>
    ///     Publish time of the newest signed feed seen so far. An older (replayed) feed is rejected, so a stale copy of a
    ///     validly signed feed cannot hide newer releases.
    /// </summary>
    [JsonProperty("lastFeedPublished", NullValueHandling = NullValueHandling.Ignore)]
    public DateTimeOffset? LastFeedPublished { get; set; }

    public static UpdateSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UpdateSettings();
            var json = File.ReadAllText(FilePath);
            return JsonConvert.DeserializeObject<UpdateSettings>(json) ?? new UpdateSettings();
        }
        catch
        {
            return new UpdateSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    public void SkipVersion(string version)
    {
        SkippedVersion = version;
        RemindAfter = null;
        Save();
    }

    public void SetRemindAfter(DateTime remindAfter)
    {
        RemindAfter = remindAfter;
        Save();
    }

    public bool ShouldShow(string version)
    {
        if (SkippedVersion == version) return false;
        if (RemindAfter.HasValue && DateTime.Now < RemindAfter.Value) return false;
        return true;
    }
}