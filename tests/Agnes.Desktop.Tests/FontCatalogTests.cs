using Agnes.App.Desktop.Themes;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.Keymaps;
using Avalonia.Media;

namespace Agnes.Desktop.Tests;

public sealed class FontCatalogTests
{
    [Fact]
    public void Blank_font_names_use_the_default_and_custom_names_are_preserved()
    {
        Assert.Equal(FontCatalog.Default, FontCatalog.Normalize(null));
        Assert.Equal(FontCatalog.Default, FontCatalog.Normalize("  "));
        Assert.Equal("Fira Code", FontCatalog.Normalize("  Fira Code "));
        Assert.Equal("Fira Code", FontCatalog.Resolve("Fira Code").ToString());
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Default", "")]
    [InlineData("Manrope", "")]
    [InlineData("JetBrainsMono", "JetBrains Mono")]
    [InlineData("Fira Code", "Fira Code")]
    public void Input_value_migrates_old_presets_and_round_trips_custom_names(string? persisted, string expected)
    {
        Assert.Equal(expected, FontCatalog.InputValue(persisted));
        Assert.IsType<FontFamily>(FontCatalog.Resolve(persisted));
    }

    [Fact]
    public void Font_and_size_preferences_round_trip_through_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agnes-font-settings-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings(FontFamily: "Fira Code", FontScale: 1.2, ChatFontScale: 1.3));

            var loaded = store.Load();

            Assert.Equal("Fira Code", loaded.FontFamily);
            Assert.Equal(1.2, loaded.FontScale);
            Assert.Equal(1.3, loaded.ChatFontScale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Free_form_font_input_applies_and_default_clears_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agnes-font-input-{Guid.NewGuid():n}");
        using var keymap = new KeymapService("[]", null, Path.Combine(directory, "keymap.json"), watch: false);
        var vm = KeymapTests.NewVm(keymap, directory);
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));

        vm.FontFamilyInput = "  Fira Code  ";
        vm.ApplyFontFamilyCommand.Execute(null);

        Assert.Equal("Fira Code", vm.FontFamily);
        Assert.Equal("Fira Code", vm.FontFamilyInput);
        Assert.Equal("Fira Code", store.Load().FontFamily);

        vm.UseDefaultFontCommand.Execute(null);

        Assert.Equal(FontCatalog.Default, vm.FontFamily);
        Assert.Equal(string.Empty, vm.FontFamilyInput);
        Assert.Equal(FontCatalog.Default, store.Load().FontFamily);
    }
}
