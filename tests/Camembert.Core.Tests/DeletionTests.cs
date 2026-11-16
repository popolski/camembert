using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.Core.Tests;

public class DeletionRulesTests
{
    static readonly DeletionRules Rules = new([@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"]);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"D:\Jeux")]                                 // tout élément à la racine d'un disque
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers")]
    [InlineData(@"c:\WINDOWS\system32\drivers\etc\hosts")]   // casse indifférente
    [InlineData(@"C:\Program Files\App\x.dll")]
    [InlineData(@"C:\Program Files (x86)\App")]
    [InlineData(@"C:\ProgramData\Microsoft\truc.dat")]
    [InlineData(@"C:\Users\bob")]
    [InlineData(@"C:\Users\bob\Documents")]
    [InlineData(@"C:\Users\bob\Documents\")]
    [InlineData(@"C:\Users\bob\NTUSER.DAT")]
    [InlineData(@"C:\Users\bob\AppData\Local")]
    [InlineData(@"C:\Users\bob\AppData\Roaming")]
    [InlineData(@"C:\Users\bob\AppData\LocalLow")]
    [InlineData(@"D:\Users\bob\Documents")]
    [InlineData(@"C:\Users\bob\Documents\..\..\..\Windows\System32\x")] // remontée par « .. »
    [InlineData(@"C:/Windows/System32")]                      // barres obliques
    [InlineData(@"C:\System Volume Information\fichier")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21\$R123.txt")]
    [InlineData(@"C:\$Extend\$UsnJrnl")]
    [InlineData(@"C:\Recovery\WindowsRE\winre.wim")]
    [InlineData(@"\\serveur\partage\dossier\fichier.txt")]
    public void Protected_locations_are_blocked(string path) => Assert.NotNull(Rules.BlockReason(path));

    [Theory]
    [InlineData(@"C:\Users\bob\Documents\vieux.zip")]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp")]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp\cache\x.tmp")]
    [InlineData(@"C:\Projets\app\node_modules")]
    [InlineData(@"D:\Jeux\Ancien\sauvegarde.bak")]
    [InlineData(@"C:\Windows.old\fichier.txt")]              // ne commence que par le même texte que C:\Windows
    [InlineData(@"C:\Program Files Perso\outil.exe")]
    public void Ordinary_locations_are_allowed(string path) => Assert.Null(Rules.BlockReason(path));

    [Fact]
    public void Rules_of_this_machine_protect_its_windows_folder()
    {
        var rules = DeletionRules.FromEnvironment();
        Assert.NotNull(rules.BlockReason(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll")));
        Assert.NotNull(rules.BlockReason(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.NotNull(rules.BlockReason(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
    }
}

public class BasketTests
{
    /// <summary>D:\ ├ a (1) ├ b (2) ├ c.bin (3, 100) │ └ d (4) └ e.bin (5, 40) └ f.bin (6, 7)</summary>
    static FileTree Sample()
    {
        var tree = new FileTree(@"D:\");
        var root = new ChildBatch();
        root.Add("a", 0, true);
        int a = tree.AddChildren(FileTree.Root, root);
        var inA = new ChildBatch();
        inA.Add("b", 0, true);
        int b = tree.AddChildren(a, inA);
        var inB = new ChildBatch();
        inB.Add("c.bin", 100, false);
        inB.Add("d", 0, true);
        int c = tree.AddChildren(b, inB);
        var inD = new ChildBatch();
        inD.Add("e.bin", 40, false);
        tree.AddChildren(c + 1, inD);
        tree.Aggregate();
        return tree;
    }

    [Fact]
    public void Child_then_parent_keeps_only_the_parent_and_counts_once()
    {
        var tree = Sample();
        var basket = new Basket(tree);
        Assert.True(basket.Add(3)); // c.bin
        Assert.True(basket.Add(5)); // e.bin
        Assert.Equal(140, basket.TotalSize);
        Assert.True(basket.Add(2)); // b contient les deux
        Assert.Equal([2], basket.Items);
        Assert.Equal(140, basket.TotalSize);
    }

    [Fact]
    public void Item_inside_a_folder_already_in_the_basket_is_not_added()
    {
        var basket = new Basket(Sample());
        Assert.True(basket.Add(2));
        Assert.False(basket.Add(5));
        Assert.False(basket.Add(2));
        Assert.True(basket.Covers(5));
        Assert.False(basket.Covers(1));
        Assert.Single(basket.Items);
    }

    [Fact]
    public void Scan_root_cannot_be_added_and_items_can_be_removed()
    {
        var basket = new Basket(Sample());
        Assert.False(basket.Add(FileTree.Root));
        basket.Add(3);
        basket.Add(4);
        Assert.True(basket.Remove(3));
        Assert.Equal([4], basket.Items);
        basket.Clear();
        Assert.Empty(basket.Items);
        Assert.Equal(0, basket.TotalSize);
    }

    [Fact]
    public void Removing_from_the_tree_updates_every_ancestor_and_hides_the_subtree()
    {
        var tree = Sample();
        tree.Remove(4); // dossier d (40)

        Assert.Equal(100, tree.Size(2));
        Assert.Equal(100, tree.Size(1));
        Assert.Equal(100, tree.Size(FileTree.Root));
        Assert.True(tree.IsRemoved(4));
        Assert.True(tree.IsRemoved(5));
        Assert.False(tree.IsRemoved(3));
        Assert.Equal([3], tree.ChildrenBySize(2));
        Assert.Equal(["c.bin"], new TreeSearch(tree).Find(new SearchQuery("bin")).Select(tree.Name));
        Assert.DoesNotContain(SunburstLayout.Build(tree, 2), s => s.Node is 4 or 5);

        tree.Remove(4); // sans effet la seconde fois
        tree.Remove(FileTree.Root);
        Assert.Equal(100, tree.Size(FileTree.Root));
    }
}
