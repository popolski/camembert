using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.Core.Tests;

public class FileTreeTests
{
    static ChildBatch Batch(params (string Name, long Size, bool IsDirectory)[] entries)
    {
        var batch = new ChildBatch();
        foreach (var (name, size, isDirectory) in entries) batch.Add(name, size, isDirectory);
        return batch;
    }

    /// <summary>C:\ ├ Users ├ hugues ├ photo.jpg (700), notes.txt (50) │ └ vide └ pagefile.sys (1000)</summary>
    static (FileTree Tree, int Users, int Hugues, int Photo) Sample()
    {
        var tree = new FileTree(@"C:\");
        int users = tree.AddChildren(FileTree.Root, Batch(("Users", 0, true), ("pagefile.sys", 1000, false)));
        int hugues = tree.AddChildren(users, Batch(("hugues", 0, true), ("vide", 0, true)));
        int photo = tree.AddChildren(hugues, Batch(("photo.jpg", 700, false), ("notes.txt", 50, false)));
        tree.Aggregate();
        return (tree, users, hugues, photo);
    }

    [Fact]
    public void Aggregate_sums_sizes_from_leaves_to_root()
    {
        var (tree, users, hugues, _) = Sample();
        Assert.Equal(750, tree.Size(hugues));
        Assert.Equal(750, tree.Size(users));
        Assert.Equal(0, tree.Size(hugues + 1)); // dossier vide
        Assert.Equal(1750, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Aggregate_is_idempotent()
    {
        var (tree, _, _, _) = Sample();
        tree.Aggregate();
        Assert.Equal(1750, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Directory_size_given_at_insertion_is_ignored()
    {
        var tree = new FileTree(@"D:\");
        int dir = tree.AddChildren(FileTree.Root, Batch(("dossier", 999, true)));
        tree.Aggregate();
        Assert.Equal(0, tree.Size(dir));
    }

    [Fact]
    public void GetPath_rebuilds_full_paths()
    {
        var (tree, users, hugues, photo) = Sample();
        Assert.Equal(@"C:\", tree.GetPath(FileTree.Root));
        Assert.Equal(@"C:\Users", tree.GetPath(users));
        Assert.Equal(@"C:\Users\hugues", tree.GetPath(hugues));
        Assert.Equal(@"C:\Users\hugues\photo.jpg", tree.GetPath(photo));
        Assert.Equal(@"C:\pagefile.sys", tree.GetPath(users + 1));
    }

    [Fact]
    public void GetPath_works_when_root_has_no_trailing_separator()
    {
        var tree = new FileTree(@"C:\Données\Été");
        int file = tree.AddChildren(FileTree.Root, Batch(("fichier accentué é.txt", 1, false)));
        Assert.Equal(@"C:\Données\Été\fichier accentué é.txt", tree.GetPath(file));
        Assert.Equal("fichier accentué é.txt", tree.Name(file));
    }

    [Fact]
    public void Children_are_contiguous_and_sorted_by_size_on_demand()
    {
        var (tree, users, hugues, photo) = Sample();
        Assert.Equal(2, tree.ChildCount(FileTree.Root));
        Assert.Equal(users, tree.FirstChild(FileTree.Root));
        Assert.Equal([users + 1, users], tree.ChildrenBySize(FileTree.Root)); // pagefile (1000) avant Users (750)
        Assert.Equal([photo, photo + 1], tree.ChildrenBySize(hugues));
        Assert.True(tree.IsDirectory(users));
        Assert.False(tree.IsDirectory(photo));
        Assert.Equal(hugues, tree.Parent(photo));
    }

    [Fact]
    public void AddChildren_twice_on_same_directory_is_rejected()
    {
        var (tree, users, _, _) = Sample();
        Assert.Throws<InvalidOperationException>(() => tree.AddChildren(users, Batch(("x", 1, false))));
    }

    [Fact]
    public void Tree_grows_beyond_initial_capacity()
    {
        var tree = new FileTree(@"C:\");
        var batch = new ChildBatch();
        for (int i = 0; i < 50_000; i++) batch.Add("fichier" + i, 2, false);
        int first = tree.AddChildren(FileTree.Root, batch);
        tree.Aggregate();
        Assert.Equal(50_001, tree.Count);
        Assert.Equal(100_000, tree.Size(FileTree.Root));
        Assert.Equal(@"C:\fichier49999", tree.GetPath(first + 49_999));
    }

    [Fact]
    public void Sunburst_angles_are_proportional_and_small_items_are_grouped()
    {
        var tree = new FileTree(@"C:\");
        var batch = Batch(("gros", 0, true), ("moyen.bin", 250, false));
        for (int i = 0; i < 100; i++) batch.Add("petit" + i, 1, false); // 100 × 0,24° : sous le seuil
        int gros = tree.AddChildren(FileTree.Root, batch);
        tree.AddChildren(gros, Batch(("a.bin", 600, false), ("b.bin", 150, false)));
        tree.Aggregate(); // total 1100

        var segments = SunburstLayout.Build(tree, FileTree.Root);
        var level1 = segments.Where(s => s.Depth == 1).ToList();
        Assert.Equal(3, level1.Count); // gros, moyen, regroupement
        Assert.Equal(gros, level1[0].Node);
        Assert.Equal(360f * 750 / 1100, level1[0].Sweep, 0.01f);
        Assert.Equal(-1, level1[2].Node);
        Assert.Equal(360f, level1.Sum(s => s.Sweep), 0.01f);

        var level2 = segments.Where(s => s.Depth == 2).ToList();
        Assert.Equal(2, level2.Count);
        Assert.Equal(level1[0].Sweep, level2.Sum(s => s.Sweep), 0.01f); // les enfants remplissent l'arc du parent
        // Teinte = angle du milieu : les enfants restent dans l'arc de teintes du parent sans lui être identiques.
        Assert.All(level2, s => Assert.InRange(s.Hue, level1[0].Start / 360f, (level1[0].Start + level1[0].Sweep) / 360f));
        Assert.NotEqual(level2[0].Hue, level2[1].Hue);
    }

    [Fact]
    public void Sunburst_of_empty_directory_is_empty()
    {
        var tree = new FileTree(@"C:\");
        tree.Aggregate();
        Assert.Empty(SunburstLayout.Build(tree, FileTree.Root));
    }

    [Theory]
    [InlineData(0, "0 octet")]
    [InlineData(1023, "1023 octets")]
    [InlineData(1536, "1,50 Ko")]
    [InlineData(1024L * 1024 * 1024 * 210, "210 Go")]
    public void Sizes_are_formatted_in_french(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormat.Format(bytes));
}
