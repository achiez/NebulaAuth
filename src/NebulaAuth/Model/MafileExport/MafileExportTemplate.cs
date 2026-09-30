namespace NebulaAuth.Model.MafileExport;

/// <summary>
///     Export template (export_templates.json). Exports never contain the Steam password: the "IncludeNebulaPassword"
///     field of older files is ignored when read and dropped on the next save.
/// </summary>
public class MafileExportTemplate
{
    public string Name { get; set; }
    public bool UseLoginAsMafileName { get; set; }
    public bool IncludeSharedSecret { get; set; }
    public bool IncludeIdentitySecret { get; set; }
    public bool IncludeRCode { get; set; }
    public bool IncludeSessionData { get; set; }
    public bool IncludeOtherInfo { get; set; }
    public bool IncludeNebulaProxy { get; set; }
    public bool IncludeNebulaGroup { get; set; }

    /// <summary>
    ///     "DeviceName" (the device name sent to Steam) in the exported mafile. Templates saved before this option existed
    ///     have no such field and include it.
    /// </summary>
    public bool IncludeDeviceName { get; set; } = true;
    public string? Path { get; set; }
    public bool ExportToZip { get; set; }
}