using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamLib.SteamMobile;

namespace NebulaAuth.NebulaLink;

/// <summary>
///     Steam Guard codes for logins (Achiefy login_code requests and NebulaAuth's own password logins). Steam accepts a
///     code for one login only, so every login of an account gets a code of a 30 s window no other login of that account
///     got: when the current window was already handed out, it waits for the next one. With less than 5 s left in the
///     current window it waits for the next window too, so the code stays valid long enough to be submitted. The app creates
///     one registry for Nebula Link and its own logins (in memory, per account key = normalized login). The code shown in the main window is not a
///     login and does not go through here. Codes are never logged (registered as runtime secrets for 90 s).
/// </summary>
public sealed class GuardCodeService
{
    public const int WINDOW_SECONDS = 30;
    public const int MIN_VALID_SECONDS = 5;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, ValueTask<long>> _steamTime;
    private readonly Dictionary<string, long> _usedWindows = new(StringComparer.Ordinal);

    public GuardCodeService(Func<CancellationToken, ValueTask<long>>? steamTime = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _steamTime = steamTime ?? (_ => TimeAligner.GetSteamTimeAsync());
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    /// <summary>
    ///     A code for one login of the account <paramref name="accountKey" />; waits (cancellable) for a window no other
    ///     login of this account used.
    /// </summary>
    /// <returns>The code and the seconds it stays valid (5..30).</returns>
    public async Task<(string Code, int ValidForSeconds)> GetCodeAsync(string accountKey, string sharedSecret,
        CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(accountKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var time = await _steamTime(ct);
            var window = time / WINDOW_SECONDS;
            var remaining = (int) (WINDOW_SECONDS - time % WINDOW_SECONDS);
            if (remaining < MIN_VALID_SECONDS)
            {
                await _delay(TimeSpan.FromSeconds(remaining), ct);
                window++;
                time = window * WINDOW_SECONDS;
                remaining = WINDOW_SECONDS;
            }

            long used;
            lock (_usedWindows) used = _usedWindows.GetValueOrDefault(accountKey, long.MinValue);
            if (used >= window)
            {
                // The code of this window already went to another login of the account: the next window
                var next = used + 1;
                await _delay(TimeSpan.FromSeconds(next * WINDOW_SECONDS - time), ct);
                window = next;
                time = window * WINDOW_SECONDS;
                remaining = WINDOW_SECONDS;
            }

            lock (_usedWindows) _usedWindows[accountKey] = window;
            var code = SteamGuardCodeGenerator.GenerateCode(sharedSecret, time);
            RuntimeSecrets.Register(code, true);
            return (code, remaining);
        }
        finally
        {
            gate.Release();
        }
    }
}
