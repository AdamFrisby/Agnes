using Agnes.App.Desktop.Themes;
using Agnes.App.Desktop.Persistence;
using Avalonia.Media;

namespace Agnes.Desktop.Tests;

public sealed class FontCatalogTests
{
    [Fact]
    public void Unknown_font_ids_fall_back_to_manrope()
    {
        Assert.Equal("Manrope", FontCatalog.Resolve(null).Id);
        Assert.Equal("Manrope", FontCatalog.Resolve("not-a-font").Id);
    }

    [Fact]
    public void Every_font_id_is_unique_and_round_trips()
    {
        Assert.Equal(FontCatalog.All.Count, FontCatalog.All.Select(font => font.Id).Distinct().Count());

        foreach (var font in FontCatalog.All)
        {
            Assert.Same(font, FontCatalog.Resolve(font.Id));
            Assert.IsType<FontFamily>(font.Family);
        }
    }

    [Fact]
    public void Font_and_size_preferences_round_trip_through_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agnes-font-settings-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings(FontFamily: "System", FontScale: 1.2));

            var loaded = store.Load();

            Assert.Equal("System", loaded.FontFamily);
            Assert.Equal(1.2, loaded.FontScale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
