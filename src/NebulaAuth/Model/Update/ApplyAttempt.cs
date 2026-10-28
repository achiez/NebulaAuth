using System;
using System.IO;
using NebulaAuth.Core;
using Newtonsoft.Json;

namespace NebulaAuth.Model.Update;

/// <summary>
///     Written to the data folder right before the updater is started. The next start compares its own version with
///     <see cref="ToVersion" />: still the old version means the installation failed (the updater restores the previous
///     version and starts it again), which is then reported to the user instead of silently looking up to date.
/// </summary>
public class ApplyAttempt
{
    public const string FILE_NAME = "apply-attempt.json";

    [JsonProperty("fromVersion")] public string FromVersion { get; set; } = string.Empty;
    [JsonProperty("toVersion")] public string ToVersion { get; set; } = string.Empty;
    [JsonProperty("channel")] public string? Channel { get; set; }
    [JsonProperty("manualUrl")] public string? ManualUrl { get; set; }
    [JsonProperty("releasePageUrl")] public string? ReleasePageUrl { get; set; }
    [JsonProperty("startedAt")] public DateTimeOffset StartedAt { get; set; }

    private static string FilePath => DataRoot.Combine(FILE_NAME);

    public void Save()
    {
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    /// <summary>
    ///     Reads and deletes the file (each attempt is reported once). Null when there is none or it is unreadable.
    /// </summary>
    public static ApplyAttempt? Take()
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;
        try
        {
            return JsonConvert.DeserializeObject<ApplyAttempt>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Shell.Logger.Warn(ex, "Unreadable {file}", FILE_NAME);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Shell.Logger.Warn(ex, "Can't delete {file}", FILE_NAME);
            }
        }
    }
}
