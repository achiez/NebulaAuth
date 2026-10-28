using System;

namespace NebulaAuth.Model.Exceptions;

/// <summary>
///     The data folder can't be written by this user: the app stops with an explanation instead of failing on the first
///     save (lost settings / accounts).
/// </summary>
public class DataFolderNotWritableException(string folder, Exception? inner)
    : Exception($"The data folder is not writable: {folder}", inner)
{
    public string Folder { get; } = folder;
}
