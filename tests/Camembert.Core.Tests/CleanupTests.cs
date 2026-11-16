using System.Diagnostics;
using Camembert.Core.Services;

namespace Camembert.Core.Tests;

/// <summary>Faux profil dans un dossier temporaire, désigné par une variable d'environnement propre au test.</summary>
public sealed class CleanupTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "CamembertTests_" + Guid.NewGuid().ToString("N"));
    static readonly DateTime Now = DateTime.UtcNow;

    public CleanupTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CAMEMBERT_TEST_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAMEMBERT_TEST_ROOT", null);
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            Directory.Delete(link);
        Directory.Delete(_root, recursive: true);
    }

    string Make(string relative, int size = 0, DateTime? modified = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, modified ?? Now.AddDays(-3));
        return path;
    }

    static CleanupCategory Category(TimeSpan minAge = default, params string[] paths) => new("t", "test", "", paths, minAge);

    [Fact]
    public void Folder_content_is_targeted_with_recursive_sizes_but_the_folder_itself_is_kept()
    {
        Make(@"Cache\a.bin", 100);
        Make(@"Cache\sous\b.bin", 50);
        Make(@"Cache\sous\plus\c.bin", 25);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, @"Cache\sous"), Now.AddDays(-3));

        var items = CleanupCatalog.FindItems(Category(default, @"%CAMEMBERT_TEST_ROOT%\Cache"), Now);

        Assert.Equal(2, items.Count);
        Assert.Equal(100, items.Single(i => !i.IsDirectory).Size);
        Assert.Equal(75, items.Single(i => i.IsDirectory).Size);
        Assert.DoesNotContain(items, i => i.Path.TrimEnd('\\').EndsWith("Cache"));
    }

    [Fact]
    public void Wildcards_expand_profile_folders_and_single_files_are_targeted_directly()
    {
        Make(@"User Data\Default\Cache\Cache_Data\f_000001", 10);
        Make(@"User Data\Profile 1\Cache\Cache_Data\f_000002", 20);
        Make(@"User Data\System Profile\Autre\x", 99); // ne correspond pas
        Make(@"MEMORY.DMP", 7);

        var items = CleanupCatalog.FindItems(Category(default,
            @"%CAMEMBERT_TEST_ROOT%\User Data\*\Cache\Cache_Data", @"%CAMEMBERT_TEST_ROOT%\MEMORY.DMP", @"%CAMEMBERT_TEST_ROOT%\absent\*"), Now);

        Assert.Equal([7L, 10L, 20L], items.Select(i => i.Size).Order());
        Assert.Contains(items, i => i.Path.EndsWith("MEMORY.DMP"));
    }

    [Fact]
    public void Recent_items_are_left_alone_when_a_minimum_age_is_set()
    {
        Make(@"Temp\vieux.tmp", 10, Now.AddDays(-2));
        Make(@"Temp\recent.tmp", 10, Now.AddHours(-1));

        var items = CleanupCatalog.FindItems(Category(TimeSpan.FromHours(24), @"%CAMEMBERT_TEST_ROOT%\Temp"), Now);

        Assert.Equal(["vieux.tmp"], items.Select(i => Path.GetFileName(i.Path)));
    }

    [Fact]
    public void Junction_inside_a_cache_is_neither_followed_nor_targeted()
    {
        Make(@"ailleurs\important.docx", 1000);
        Make(@"Cache\a.bin", 5);
        Make(@"Cache\dossier\b.bin", 6);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, @"Cache\dossier"), Now.AddDays(-3));
        foreach (var (link, target) in new[] { (@"Cache\jonction", "ailleurs"), (@"Cache\dossier\jonction", "ailleurs") })
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_root, link)}\" \"{Path.Combine(_root, target)}\"")
                   { CreateNoWindow = true, UseShellExecute = false })!)
                mklink.WaitForExit();
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, @"Cache\dossier"), Now.AddDays(-3));

        var items = CleanupCatalog.FindItems(Category(default, @"%CAMEMBERT_TEST_ROOT%\Cache"), Now);

        Assert.DoesNotContain(items, i => i.Path.EndsWith("jonction"));
        Assert.Equal(11, items.Sum(i => i.Size)); // important.docx n'est compté nulle part
    }

    [Fact]
    public void Catalog_targets_only_locations_allowed_by_the_deletion_rules_except_those_left_to_windows()
    {
        var rules = DeletionRules.FromEnvironment();
        foreach (var category in CleanupCatalog.Categories)
            foreach (string pattern in category.Paths)
            {
                // Un élément type sous chaque emplacement : autorisé pour les catégories nettoyées par Camembert,
                // protégé pour celles laissées à Windows.
                string sample = Path.Combine(Environment.ExpandEnvironmentVariables(pattern).Replace("*", "Default"), "element");
                Assert.True((rules.BlockReason(sample) is null) == (category.ByWindows is null), $"{category.Id} : {sample}");
            }
    }
}
