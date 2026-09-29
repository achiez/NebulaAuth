using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using NebulaAuth.Core;
using Newtonsoft.Json;

namespace NebulaAuth.Model.MafileExport;

public static class MafileExporterStorage
{
    private const string FILE_NAME = "export_templates.json";
    public static ObservableCollection<MafileExportTemplate> Templates { get; } = new();

    private static string FilePath => DataRoot.Combine(FILE_NAME);


    public static void Initialize()
    {
        LoadOrCreateTemplate();
    }

    public static MafileExportTemplate? GetTemplate(string name)
    {
        return Templates.FirstOrDefault(x => x.Name == name);
    }

    public static void AddTemplate(MafileExportTemplate template)
    {
        Templates.Add(template);
        Save();
    }

    public static void DeleteTemplate(MafileExportTemplate template)
    {
        if (Templates.Remove(template))
        {
            Save();
        }
    }

    private static void LoadOrCreateTemplate()
    {
        if (!File.Exists(FilePath))
        {
            Templates.Clear();
            foreach (var mafileExportTemplate in CreateDefaultTemplate())
            {
                Templates.Add(mafileExportTemplate);
            }

            Save();
            return;
        }

        var json = File.ReadAllText(FilePath);
        try
        {
            var loadedTemplates = JsonConvert.DeserializeObject<List<MafileExportTemplate>>(json);
            if (loadedTemplates != null)
            {
                Templates.Clear();
                foreach (var tmpl in loadedTemplates)
                {
                    Templates.Add(tmpl);
                }
            }
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex, $"Failed to deserialize {FILE_NAME}");
            Templates.Clear();
            Save();
        }
    }

    public static void Save()
    {
        var json = JsonConvert.SerializeObject(Templates, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    private static IEnumerable<MafileExportTemplate> CreateDefaultTemplate()
    {
        return
        [
            new MafileExportTemplate
            {
                Name = "Default",
                UseLoginAsMafileName = false,
                IncludeSharedSecret = true,
                IncludeIdentitySecret = true,
                IncludeRCode = true,
                IncludeSessionData = true,
                IncludeOtherInfo = true,
                IncludeNebulaProxy = true,
                IncludeNebulaPassword = true,
                IncludeNebulaGroup = true,
                Path = null
            },
            new MafileExportTemplate
            {
                Name = "Auth only",
                UseLoginAsMafileName = true,
                IncludeSharedSecret = true,
                IncludeIdentitySecret = false,
                IncludeRCode = false,
                IncludeSessionData = false,
                IncludeOtherInfo = false,
                IncludeNebulaProxy = false,
                IncludeNebulaPassword = false,
                IncludeNebulaGroup = false,
                Path = null
            }
        ];
    }
}