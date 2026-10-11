using System;
using System.Threading;
using Avalonia.Threading;
using NebulaAuth.NebulaLink;

namespace NebulaAuth.Model.LinkHost;

/// <summary>What changed in Nebula Link since the last UI update (several kinds at once).</summary>
public readonly record struct NebulaLinkUiChange(bool State, bool Accounts, bool History);

/// <summary>
///     The only bridge from Nebula Link events (raised on any thread, often in bursts: one confirm on 50 operations writes
///     the history many times) to the UI: changes are collected and delivered on the UI thread at most once per
///     <see cref="Delay" />.
/// </summary>
public sealed class NebulaLinkUiEvents : IDisposable
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);

    private readonly NebulaLinkIntegration _integration;
    private bool _disposed;
    private int _pending;
    private int _queued;

    public NebulaLinkUiEvents(NebulaLinkIntegration integration)
    {
        _integration = integration;
        integration.Changed += OnChanged;
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<NebulaLinkUiChange>? Changed;

    public void Dispose()
    {
        _disposed = true;
        _integration.Changed -= OnChanged;
    }

    private void OnChanged(NebulaLinkChange change)
    {
        Interlocked.Or(ref _pending, 1 << (int) change);
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        // A DispatcherTimer can only be started on the UI thread
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(Flush, Delay));
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _queued, 0);
        var flags = Interlocked.Exchange(ref _pending, 0);
        if (_disposed || flags == 0) return;
        Changed?.Invoke(new NebulaLinkUiChange(
            (flags & (1 << (int) NebulaLinkChange.State)) != 0,
            (flags & (1 << (int) NebulaLinkChange.Accounts)) != 0,
            (flags & (1 << (int) NebulaLinkChange.History)) != 0));
    }
}
