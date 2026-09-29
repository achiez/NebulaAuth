using System;

namespace NebulaAuth.Core;

public class SnackbarController
{
    private const int MIN_SNACKBAR_TIME = 1200;

    public static SnackbarMessageQueue MessageQueue { get; } = new() {DiscardDuplicates = true};

    /// <summary>
    /// </summary>
    /// <param name="text"></param>
    /// <param name="duration">Default duration is 1 second</param>
    public static void SendSnackbar(string text, TimeSpan? duration = null)
    {
        duration ??= GetSnackbarTime(text);
        MessageQueue.Enqueue(new SnackbarMessage(text, null, null, duration.Value));
    }

    /// <summary>
    /// </summary>
    /// <param name="text"></param>
    /// <param name="action"></param>
    /// <param name="duration">Default duration is 1 second</param>
    /// <param name="actionText">Default: 'OK'</param>
    public static void SendSnackbarWithButton(string text, string actionText = "OK", Action? action = null,
        TimeSpan? duration = null)
    {
        duration ??= GetSnackbarTime(text);
        MessageQueue.Enqueue(new SnackbarMessage(text, actionText, action ?? (() => { }), duration.Value));
    }

    private static TimeSpan GetSnackbarTime(string str)
    {
        var duration = str.Length / 0.022;
        if (duration < MIN_SNACKBAR_TIME)
        {
            duration = MIN_SNACKBAR_TIME;
        }

        return TimeSpan.FromMilliseconds(duration);
    }
}
