using System.Collections.Generic;
using System.Linq;

namespace NebulaAuth.Model.Mafiles.Protection;

/// <summary>
///     Progress of a bulk storage operation (startup reconciliation, enabling / disabling encryption).
/// </summary>
public readonly record struct StorageProgress(int Processed, int Total, string? CurrentFile);

/// <summary>
///     Result of a bulk storage operation.
/// </summary>
public sealed class StorageOperationResult
{
    /// <summary>Files examined.</summary>
    public int Total { get; internal set; }

    /// <summary>Files converted to the other format (or rewritten without the legacy password field).</summary>
    public int Converted { get; internal set; }

    /// <summary>Both versions of a file existed with the same content: the source version was removed.</summary>
    public int Deduplicated { get; internal set; }

    /// <summary>Files left in place and not converted (invalid, other storage, conflicts...).</summary>
    public List<SkippedFile> Skipped { get; } = [];

    /// <summary>
    ///     Files that could be converted but the write / verification failed. While this is not empty an
    ///     enable / disable operation is not finished (the mode stays encrypting / decrypting and is resumed).
    /// </summary>
    public List<SkippedFile> Failed { get; } = [];

    /// <summary>The storage reached its final mode (<see cref="StorageMode.Plain" /> / <see cref="StorageMode.Encrypted" />).</summary>
    public bool Completed { get; internal set; }

    public StorageMode ModeAfter { get; internal set; }

    public IEnumerable<SkippedFile> AllSkipped => Skipped.Concat(Failed);
}
