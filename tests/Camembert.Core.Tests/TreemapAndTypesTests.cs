using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.Core.Tests;

public class TreemapAndTypesTests
{
    /// <summary>D:\ ├ Vidéos ├ a.MP4 (600) ├ b.mkv (200) │ └ notes (3) ├ c.mp4 (150) ├ LICENSE (40) └ 100 × petit.txt (1)</summary>
    static FileTree Sample()
    {
        var tree = new FileTree(@"D:\");
        var root = new ChildBatch();
        root.Add("Vidéos", 0, true);
        root.Add("c.mp4", 150, false);
        root.Add("LICENSE", 40, false);
        for (int i = 0; i < 100; i++) root.Add($"petit{i}.txt", 1, false);
        int videos = tree.AddChildren(FileTree.Root, root);
        var inVideos = new ChildBatch();
        inVideos.Add("a.MP4", 600, false);
        inVideos.Add("b.mkv", 200, false);
        inVideos.Add("notes", 3, false);
        tree.AddChildren(videos, inVideos);
        tree.Aggregate(); // total 1093
        return tree;
    }

    [Fact]
    public void Types_are_totalled_by_lowercase_extension_and_ranked_by_size()
    {
        var tree = Sample();
        var stats = FileTypeStats.Compute(tree);

        Assert.Equal([".mp4", ".mkv", ".txt", "", ], stats.Totals.Select(t => t.Extension));
        Assert.Equal(new FileTypeTotal(".mp4", 750, 2), stats.Totals[0]);
        Assert.Equal(new FileTypeTotal("", 43, 2), stats.Totals[3]); // LICENSE et notes
        Assert.Equal(1093, stats.Totals.Sum(t => t.Size));
        Assert.Equal(0, stats.RankOf(tree, Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == "a.MP4")));
        Assert.Equal(-1, stats.RankOf(tree, 1)); // dossier
    }

    [Fact]
    public void Removed_files_leave_the_type_totals()
    {
        var tree = Sample();
        tree.Remove(1); // Vidéos
        var stats = FileTypeStats.Compute(tree);
        Assert.Equal(new FileTypeTotal(".mp4", 150, 1), stats.Totals[0]);
        Assert.DoesNotContain(stats.Totals, t => t.Extension == ".mkv");
    }

    [Theory]
    [InlineData("Film.MKV", ".mkv")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData(".gitignore", "")]
    [InlineData("LICENSE", "")]
    [InlineData("point.", "")]
    [InlineData("nom.extensiontroplongue", "")]
    public void Extension_follows_the_type_column_rule(string name, string expected) =>
        Assert.Equal(expected, FileTypeStats.ExtensionOf(name));

    [Fact]
    public void Treemap_areas_are_proportional_tile_the_space_and_stay_inside_their_folder()
    {
        var tree = Sample();
        const float width = 1093, height = 100; // 100 pixels par octet : aires faciles à vérifier
        var rects = TreemapLayout.Build(tree, FileTree.Root, width, height, minArea: 50);

        var top = rects.Where(r => r.Depth == 1).ToList();
        Assert.Equal(width * height, top.Sum(r => r.Width * r.Height), 1f); // le premier niveau couvre tout
        Assert.All(top, r => Assert.True(r.X >= -0.01 && r.Y >= -0.01 && r.X + r.Width <= width + 0.01 && r.Y + r.Height <= height + 0.01));
        var videos = top.Single(r => r.Node == 1);
        Assert.Equal(803 * 100f, videos.Width * videos.Height, 1f);
        Assert.False(videos.Leaf); // dossier détaillé

        // Contenu de Vidéos : dans son rectangle (moins la marge), aires dans les mêmes proportions.
        var inside = rects.Where(r => r.Depth == 2).ToList();
        Assert.Equal(3, inside.Count);
        Assert.All(inside, r => Assert.True(r.X >= videos.X && r.Y >= videos.Y && r.X + r.Width <= videos.X + videos.Width + 0.01 && r.Y + r.Height <= videos.Y + videos.Height + 0.01));
        var a = inside.Single(r => tree.Name(r.Node) == "a.MP4");
        Assert.Equal(600.0 / 803, a.Width * a.Height / inside.Sum(r => r.Width * r.Height), 3);
    }

    [Fact]
    public void Elements_too_small_to_draw_are_grouped_into_one_rectangle()
    {
        var tree = Sample();
        var rects = TreemapLayout.Build(tree, FileTree.Root, 1093, 100, minArea: 150); // un fichier d'1 octet = 100 px
        var top = rects.Where(r => r.Depth == 1).ToList();
        Assert.Equal(4, top.Count); // Vidéos, c.mp4, LICENSE, regroupement
        var group = top.Single(r => r.Node == -1);
        Assert.Equal(100 * 100f, group.Width * group.Height, 1f);
        Assert.True(group.Leaf);
    }

    [Fact]
    public void Rectangles_stay_reasonably_square_and_their_count_is_bounded_by_the_screen()
    {
        var tree = new FileTree(@"C:\");
        var batch = new ChildBatch();
        var random = new Random(7);
        for (int i = 0; i < 20_000; i++) batch.Add($"f{i}.bin", random.Next(1, 1_000_000), false);
        tree.AddChildren(FileTree.Root, batch);
        tree.Aggregate();

        var rects = TreemapLayout.Build(tree, FileTree.Root, 800, 600, minArea: 24);

        Assert.True(rects.Count <= 800 * 600 / 24 + 1);
        var drawn = rects.Where(r => r.Node >= 0).ToList();
        double medianRatio = drawn.Select(r => Math.Max(r.Width / r.Height, r.Height / r.Width)).Order().ElementAt(drawn.Count / 2);
        Assert.True(medianRatio < 3, $"rapport médian {medianRatio:0.0}");
    }

    [Fact]
    public void Empty_folder_gives_no_rectangle()
    {
        var tree = new FileTree(@"C:\");
        tree.Aggregate();
        Assert.Empty(TreemapLayout.Build(tree, FileTree.Root, 100, 100));
    }
}
