using System;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaAuth.Model.Mafiles;

public enum ImportPhase
{
    Idle,

    /// <summary>Getting the files (file picker, clipboard).</summary>
    Selecting,

    /// <summary>Checking, confirming and writing the selected files.</summary>
    Importing
}

/// <summary>
///     The single entry of the mafile import (menu, drag and drop, clipboard): one import at a time. The gate is taken
///     synchronously, before the first await, and released in <c>finally</c>, so a trigger that arrives while files
///     are read, checked, confirmed or written is rejected instead of starting a second import or dialog.
/// </summary>
public sealed class ImportGate
{
    private int _busy;
    private ImportPhase _phase;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public ImportPhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value) return;
            _phase = value;
            PhaseChanged?.Invoke();
        }
    }

    /// <summary>Raised on the thread that runs the import (UI thread in the app).</summary>
    public event Action? PhaseChanged;

    /// <summary>
    ///     Runs <paramref name="getFiles" />, then <paramref name="import" /> with the files (skipped when there are none).
    /// </summary>
    /// <returns>False when another import is running (nothing was started).</returns>
    public async Task<bool> RunAsync(Func<Task<string[]>> getFiles, Func<string[], Task> import)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try
        {
            Phase = ImportPhase.Selecting;
            var files = await getFiles();
            if (files.Length == 0) return true;
            Phase = ImportPhase.Importing;
            await import(files);
            return true;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Phase = ImportPhase.Idle;
        }
    }
}
