using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.Core.Tests;

public class TreeSearchTests
{
    static readonly long T1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    static readonly long T2 = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    static readonly long T3 = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    /// <summary>
    /// C:\ ├ Vidéos ├ Été 2025.MP4 (5 Go) │ ├ film.mkv (2 Go) │ └ notes.txt (10)
    ///     ├ Photos ├ plage.JPG (4 Mo) │ └ été.png (2 Mo)
    ///     ├ rapport.pdf (300 Ko)  └ archive.tar.gz (1 Mo)
    /// </summary>
    static FileTree Sample()
    {
        var tree = new FileTree(@"C:\");
        var root = new ChildBatch();
        root.Add("Vidéos", 0, true, T1);
        root.Add("Photos", 0, true, T2);
        root.Add("rapport.pdf", 300 << 10, false, T3);
        root.Add("archive.tar.gz", 1 << 20, false, T1);
        int videos = tree.AddChildren(FileTree.Root, root);
        var a = new ChildBatch();
        a.Add("Été 2025.MP4", 5L << 30, false, T2);
        a.Add("film.mkv", 2L << 30, false, T1);
        a.Add("notes.txt", 10, false, T3);
        tree.AddChildren(videos, a);
        var b = new ChildBatch();
        b.Add("plage.JPG", 4 << 20, false, T2);
        b.Add("été.png", 2 << 20, false, T1);
        tree.AddChildren(videos + 1, b);
        tree.Aggregate();
        return tree;
    }

    static string[] Names(FileTree tree, int[] ids) => ids.Select(tree.Name).Order(StringComparer.Ordinal).ToArray();

    static string[] Find(SearchQuery query)
    {
        var tree = Sample();
        return Names(tree, new TreeSearch(tree).Find(query));
    }

    [Fact]
    public void Name_search_is_partial_and_ignores_case_including_accents()
    {
        Assert.Equal(["Été 2025.MP4", "été.png"], Find(new SearchQuery("ÉTÉ")));
        Assert.Equal(["Été 2025.MP4", "été.png"], Find(new SearchQuery("  été ")));
        Assert.Equal(["plage.JPG"], Find(new SearchQuery("PLA")));
    }

    [Fact]
    public void Name_search_never_matches_across_two_neighbouring_names_or_the_root()
    {
        // « Vidéos » et « Photos » se suivent en mémoire : « osPh » n'existe dans aucun nom.
        Assert.Empty(Find(new SearchQuery("osph")));
        Assert.Empty(Find(new SearchQuery(@"c:\")));
        Assert.Empty(Find(new SearchQuery("introuvable")));
    }

    [Fact]
    public void Extension_filter_accepts_several_forms_and_only_returns_files()
    {
        Assert.Equal(["film.mkv", "Été 2025.MP4"], Find(new SearchQuery(Extensions: "mp4, .MKV")));
        Assert.Equal(["archive.tar.gz"], Find(new SearchQuery(Extensions: "*.gz")));
        Assert.Equal(["archive.tar.gz"], Find(new SearchQuery(Extensions: "tar.gz")));
        Assert.Empty(Find(new SearchQuery(Extensions: "os"))); // « Vidéos » est un dossier
    }

    [Fact]
    public void Category_size_and_kind_filters_combine()
    {
        Assert.Equal(["plage.JPG", "été.png"], Find(new SearchQuery(Category: FileCategory.Images)));
        Assert.Equal(["Été 2025.MP4"], Find(new SearchQuery(Category: FileCategory.Videos, MinSize: 3L << 30)));
        Assert.Equal(["Photos", "Vidéos"], Find(new SearchQuery(Kind: EntryKind.Directories)));
        Assert.Equal(["Vidéos"], Find(new SearchQuery(Kind: EntryKind.Directories, MinSize: 1L << 30))); // taille agrégée du dossier
        Assert.Equal(["notes.txt", "rapport.pdf"], Find(new SearchQuery(MaxSize: (1 << 20) - 1, Kind: EntryKind.Files)));
        Assert.Equal(["été.png"], Find(new SearchQuery("été", Category: FileCategory.Images)));
    }

    [Fact]
    public void Empty_query_is_reported_as_empty()
    {
        Assert.True(new SearchQuery().IsEmpty);
        Assert.True(new SearchQuery("   ").IsEmpty);
        Assert.False(new SearchQuery(MinSize: 1).IsEmpty);
        Assert.False(new SearchQuery(Kind: EntryKind.Files).IsEmpty);
    }

    [Fact]
    public void Results_sort_by_name_size_and_date_in_both_directions()
    {
        var tree = Sample();
        var ids = new TreeSearch(tree).Find(new SearchQuery(Kind: EntryKind.Files));

        TreeSearch.Sort(tree, ids, SortKey.Size, descending: true);
        Assert.Equal(["Été 2025.MP4", "film.mkv", "plage.JPG"], ids.Take(3).Select(tree.Name));
        TreeSearch.Sort(tree, ids, SortKey.Size, descending: false);
        Assert.Equal("notes.txt", tree.Name(ids[0]));
        TreeSearch.Sort(tree, ids, SortKey.Name, descending: false);
        Assert.Equal(["archive.tar.gz", "film.mkv", "notes.txt"], ids.Take(3).Select(tree.Name));
        TreeSearch.Sort(tree, ids, SortKey.Modified, descending: true);
        Assert.Equal(["notes.txt", "rapport.pdf"], ids.Take(2).Select(tree.Name).Order());
    }

    [Fact]
    public void Cancelled_search_throws()
    {
        var tree = Sample();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var big = new FileTree(@"C:\");
        var batch = new ChildBatch();
        for (int i = 0; i < 200_000; i++) batch.Add("f" + i, 1, false);
        big.AddChildren(FileTree.Root, batch);
        Assert.Throws<OperationCanceledException>(() => new TreeSearch(big).Find(new SearchQuery(MinSize: 1), cancellation.Token));
    }

    [Fact]
    public void Tree_survives_a_write_and_read_round_trip()
    {
        var tree = Sample();
        using var stream = new MemoryStream();
        tree.Write(stream);
        stream.Position = 0;
        var copy = FileTree.Read(stream);

        Assert.Equal(tree.Count, copy.Count);
        for (int i = 0; i < tree.Count; i++)
        {
            Assert.Equal(tree.GetPath(i), copy.GetPath(i));
            Assert.Equal(tree.Size(i), copy.Size(i));
            Assert.Equal(tree.IsDirectory(i), copy.IsDirectory(i));
            Assert.Equal(tree.Modified(i), copy.Modified(i));
            Assert.Equal(tree.ChildrenBySize(i), copy.ChildrenBySize(i));
        }
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), copy.Modified(3));
    }

    [Fact]
    public void Corrupted_or_foreign_tree_data_is_rejected()
    {
        var tree = Sample();
        using var stream = new MemoryStream();
        tree.Write(stream);
        var bytes = stream.ToArray();

        Assert.Throws<InvalidDataException>(() => FileTree.Read(new MemoryStream(new byte[64])));          // mauvais en-tête
        Assert.Throws<EndOfStreamException>(() => FileTree.Read(new MemoryStream(bytes, 0, bytes.Length / 2))); // tronqué
        var cyclic = (byte[])bytes.Clone();
        BitConverter.GetBytes(5).CopyTo(cyclic, 12 + 4); // parent du nœud 1 = nœud 5 : un parent doit précéder ses enfants
        Assert.Throws<InvalidDataException>(() => FileTree.Read(new MemoryStream(cyclic)));
    }

    [Theory]
    [InlineData("500 Mo", 500L << 20)]
    [InlineData("1,5 Go", 3L << 29)]
    [InlineData("1.5go", 3L << 29)]
    [InlineData("200 ko", 200L << 10)]
    [InlineData("12", 12L << 20)]
    [InlineData("42 octets", 42)]
    public void Size_text_is_parsed(string text, long expected)
    {
        Assert.True(SizeFormat.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("gros")]
    [InlineData("12 parsecs")]
    [InlineData("")]
    public void Invalid_size_text_is_rejected(string text) => Assert.False(SizeFormat.TryParse(text, out _));

    [Fact]
    public void Type_column_describes_files_and_directories()
    {
        Assert.Equal("Dossier", FileTypes.Describe("Photos.old", true));
        Assert.Equal("Fichier PNG", FileTypes.Describe("été.png", false));
        Assert.Equal("Fichier", FileTypes.Describe("LICENSE", false));
        Assert.Equal("Fichier", FileTypes.Describe(".gitignore", false));
    }
}
