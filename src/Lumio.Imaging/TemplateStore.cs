using System.Text.Json;

namespace Lumio.Imaging;

public sealed class TemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _templatesDirectory;

    public TemplateStore(string? customDirectory = null)
    {
        _templatesDirectory = customDirectory 
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumio", "Templates");
        
        EnsureDefaultTemplates();
    }

    public string TemplatesDirectory => _templatesDirectory;

    public void EnsureDefaultTemplates()
    {
        Directory.CreateDirectory(_templatesDirectory);

        var defaults = new[]
        {
            BuiltInTemplates.CreateSinglePhotoPostcard(),
            BuiltInTemplates.CreateQuadCollage(),
            BuiltInTemplates.CreateDoubleStripForSelphy(),
        };

        foreach (var t in defaults)
        {
            var file = Path.Combine(_templatesDirectory, $"{t.Id}.json");
            if (!File.Exists(file))
            {
                Save(t);
            }
        }
    }

    public IReadOnlyList<BoothTemplate> GetAllTemplates()
    {
        Directory.CreateDirectory(_templatesDirectory);
        var list = new List<BoothTemplate>();

        foreach (var file in Directory.EnumerateFiles(_templatesDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var template = JsonSerializer.Deserialize<BoothTemplate>(json, JsonOptions);
                if (template is not null)
                {
                    list.Add(template);
                }
            }
            catch
            {
                // Skip invalid template file
            }
        }

        if (list.Count == 0)
        {
            list.Add(BuiltInTemplates.CreateSinglePhotoPostcard());
        }

        return list;
    }

    public BoothTemplate? GetById(string id)
    {
        return GetAllTemplates().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? GetAllTemplates().FirstOrDefault();
    }

    public void Save(BoothTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Directory.CreateDirectory(_templatesDirectory);

        var file = Path.Combine(_templatesDirectory, $"{template.Id}.json");
        var json = JsonSerializer.Serialize(template, JsonOptions);
        File.WriteAllText(file, json);
    }

    public void Delete(string id)
    {
        var file = Path.Combine(_templatesDirectory, $"{id}.json");
        if (File.Exists(file))
            File.Delete(file);
    }
}
