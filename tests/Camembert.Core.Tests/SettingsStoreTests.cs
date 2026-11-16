using Camembert.Core.Services;

namespace Camembert.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "CamembertTests_" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void First_launch_gives_defaults_following_windows_theme()
    {
        var settings = new SettingsStore(_directory).Load();
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.True(settings.Animations);
    }

    [Fact]
    public void Saved_settings_are_read_back_from_a_readable_json_file()
    {
        var store = new SettingsStore(_directory);
        Assert.Null(store.Save(new AppSettings { Theme = AppTheme.Light, Animations = false }));

        Assert.Equal(new AppSettings { Theme = AppTheme.Light, Animations = false }, new SettingsStore(_directory).Load());
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"Theme\": \"Light\"", json); // lisible et modifiable à la main
        Assert.Equal(Path.Combine(_directory, "Camembert.settings.json"), store.FilePath);
    }

    [Theory]
    [InlineData("ceci n'est pas du JSON")]
    [InlineData("{ \"Theme\": \"Fluo\" }")]
    [InlineData("null")]
    [InlineData("")]
    public void Corrupted_file_falls_back_to_defaults(string content)
    {
        var store = new SettingsStore(_directory);
        File.WriteAllText(store.FilePath, content);
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void Unknown_properties_and_missing_ones_are_tolerated()
    {
        var store = new SettingsStore(_directory);
        File.WriteAllText(store.FilePath, "{ \"Animations\": false, \"ReglageFutur\": 3 }");
        Assert.Equal(new AppSettings { Animations = false }, store.Load());
    }

    [Fact]
    public void Unwritable_folder_is_reported_instead_of_throwing()
    {
        var store = new SettingsStore(Path.Combine(_directory, "dossier", "inexistant"));
        Assert.NotNull(store.Save(new AppSettings()));

        // Fichier en lecture seule : cas d'un dossier d'installation protégé.
        var locked = new SettingsStore(_directory);
        File.WriteAllText(locked.FilePath, "{}");
        File.SetAttributes(locked.FilePath, FileAttributes.ReadOnly);
        try { Assert.NotNull(locked.Save(new AppSettings())); }
        finally { File.SetAttributes(locked.FilePath, FileAttributes.Normal); }
    }
}
