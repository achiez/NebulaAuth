using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NebulaAuth.Model;

namespace NebulaAuth.Utility;

/// <summary>
///     Deletes old daily log files of the app ("log-yyyy-MM-dd.log", written by NLog.config) from the logs folder.
///     Other files in that folder are never touched.
/// </summary>
public static partial class LogRetention
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);

    /// <returns>Number of deleted files.</returns>
    public static int Cleanup(string logDirectory, TimeSpan maxAge, DateTime now)
    {
        if (!Directory.Exists(logDirectory)) return 0;
        var cutoff = now.Date - maxAge;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(logDirectory, "log-*.log"))
        {
            var match = DailyLogName().Match(Path.GetFileName(path));
            if (!match.Success ||
                !DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                continue;
            // The name gives the day; the write time protects a file that is still in use (e.g. a wrong clock)
            if (date >= cutoff || File.GetLastWriteTime(path) >= cutoff) continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Shell.Logger.Warn("Log retention: can't delete {file}: {error}", Path.GetFileName(path), ex.Message);
            }
        }

        if (deleted > 0)
            Shell.Logger.Info("Log retention: deleted {count} log file(s) older than {days} days", deleted,
                (int) maxAge.TotalDays);
        return deleted;
    }

    [GeneratedRegex(@"^log-(?<date>\d{4}-\d{2}-\d{2})\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex DailyLogName();
}
