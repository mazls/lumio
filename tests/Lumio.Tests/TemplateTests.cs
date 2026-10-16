using Lumio.Imaging;

namespace Lumio.Tests;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public TemplateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lumio_templates_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void EnsureDefaultTemplates_CreatesDefaultTemplatesOnDisk()
    {
        var store = new TemplateStore(_tempDir);
        var templates = store.GetAllTemplates();

        Assert.NotEmpty(templates);
        Assert.Contains(templates, t => t.Id == "single-10x15");
        Assert.Contains(templates, t => t.Id == "quad-10x15");
        Assert.Contains(templates, t => t.Id == "double-strip-10x15");
    }

    [Fact]
    public void SaveAndGetById_PersistsCustomTemplate()
    {
        var store = new TemplateStore(_tempDir);
        var custom = new BoothTemplate
        {
            Id = "custom-vip-card",
            Name = "VIP Geburtstagskarte",
            Width = 1800,
            Height = 1200,
            BackgroundColorHex = "#FF2244",
            Slots = [new TemplateSlot { Index = 0, X = 100, Y = 100, Width = 800, Height = 600 }]
        };

        store.Save(custom);
        var loaded = store.GetById("custom-vip-card");

        Assert.NotNull(loaded);
        Assert.Equal("VIP Geburtstagskarte", loaded.Name);
        Assert.Equal("#FF2244", loaded.BackgroundColorHex);
        Assert.Single(loaded.Slots);
    }

    [Fact]
    public void Delete_RemovesTemplateFile()
    {
        var store = new TemplateStore(_tempDir);
        var custom = new BoothTemplate { Id = "to-delete", Name = "Lösch mich" };
        store.Save(custom);

        Assert.NotNull(store.GetById("to-delete"));

        store.Delete("to-delete");
        Assert.DoesNotContain(store.GetAllTemplates(), t => t.Id == "to-delete");
    }
}
