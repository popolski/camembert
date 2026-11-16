using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;
using Camembert.Scanner.Standard;

namespace Camembert.Scanner.Tests;

public sealed class AdminFactAttribute : FactAttribute
{
    /// <summary>Test ignoré (et signalé comme tel) hors d'une session administrateur.</summary>
    public AdminFactAttribute()
    {
        if (!Environment.IsPrivilegedProcess) Skip = "Droits d'administrateur requis (lecture de la MFT).";
    }
}

/// <summary>
/// Arborescence réelle dans un dossier temporaire :
/// racine ├ a ├ un.bin (1000) │ └ sous └ deux.bin (500)
///        ├ b.bin (200)
///        ├ jonction → racine (boucle)      ├ lien_dossier → a      ├ lien_fichier → a\un.bin
///        └ interdit (lecture refusée) └ secret.bin (300)
/// </summary>
public sealed class StandardScanEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "CamembertTests_" + Guid.NewGuid().ToString("N"));
    readonly string _denied;
    readonly FileSystemAccessRule _denyRule;
    readonly bool _symlinksCreated;

    public StandardScanEngineTests()
    {
        string a = Path.Combine(_root, "a");
        Directory.CreateDirectory(Path.Combine(a, "sous"));
        File.WriteAllBytes(Path.Combine(a, "un.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(a, "sous", "deux.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(_root, "b.bin"), new byte[200]);

        // Jonction : ne demande pas de privilège. Elle pointe vers la racine, donc boucle infinie si elle est suivie.
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_root, "jonction")}\" \"{_root}\"")
               { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        // Liens symboliques : exigent le mode développeur ou des droits administrateur.
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "lien_dossier"), a);
            File.CreateSymbolicLink(Path.Combine(_root, "lien_fichier"), Path.Combine(a, "un.bin"));
            _symlinksCreated = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        _denied = Path.Combine(_root, "interdit");
        Directory.CreateDirectory(_denied);
        File.WriteAllBytes(Path.Combine(_denied, "secret.bin"), new byte[300]);
        _denyRule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(_denied);
        var security = info.GetAccessControl();
        security.AddAccessRule(_denyRule);
        info.SetAccessControl(security);
    }

    public void Dispose()
    {
        var info = new DirectoryInfo(_denied);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(_denyRule);
        info.SetAccessControl(security);
        // Supprimer les liens d'abord, sans jamais les traverser.
        foreach (string link in new[] { "jonction", "lien_dossier" })
            if (Directory.Exists(Path.Combine(_root, link))) Directory.Delete(Path.Combine(_root, link));
        Directory.Delete(_root, recursive: true);
    }

    static string[] ChildNames(FileTree tree, int node) =>
        Enumerable.Range(tree.FirstChild(node), tree.ChildCount(node)).Select(tree.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Scan_counts_each_file_once_and_survives_links_and_denied_directory()
    {
        var progress = new ScanProgress();
        // Délai dépassé = l'analyse boucle (jonction suivie).
        var tree = await Task.Run(() => new StandardScanEngine().Scan(_root, progress, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30));

        // Ni boucle ni double comptage : seuls un.bin, deux.bin et b.bin comptent.
        Assert.Equal(1700, tree.Size(FileTree.Root));
        Assert.Equal(3, progress.Files);
        Assert.Equal(1700, progress.Bytes);
        Assert.Equal(["a", "b.bin", "interdit"], ChildNames(tree, FileTree.Root));

        // Dossier inaccessible : signalé, compté pour 0, sans interrompre le reste.
        Assert.Equal(1, progress.Errors);
        int denied = Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == "interdit");
        Assert.True(tree.IsInaccessible(denied));
        Assert.Equal(0, tree.Size(denied));

        int a = Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == "a");
        Assert.Equal(1500, tree.Size(a));
        int deux = Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == "deux.bin");
        Assert.Equal(Path.Combine(_root, "a", "sous", "deux.bin"), tree.GetPath(deux));
    }

    [AdminFact]
    public void Mft_scan_of_the_same_folder_skips_links_and_counts_a_hard_link_once()
    {
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{Path.Combine(_root, "b_lien.bin")}\" \"{Path.Combine(_root, "b.bin")}\"")
               { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();

        var progress = new ScanProgress();
        var tree = new Ntfs.NtfsScanEngine().Scan(_root, progress, CancellationToken.None);

        // La MFT ignore les droits d'accès : le contenu du dossier « interdit » est vu (300 octets).
        Assert.Equal(["a", "b.bin", "b_lien.bin", "interdit"], ChildNames(tree, FileTree.Root));
        Assert.Equal(2000, tree.Size(FileTree.Root));
        Assert.Equal(4, progress.Files);
        int Node(string name) => Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == name);
        Assert.Equal([0L, 200L], new[] { tree.Size(Node("b.bin")), tree.Size(Node("b_lien.bin")) }.Order());
        Assert.Equal(1500, tree.Size(Node("a")));
        Assert.Equal(300, tree.Size(Node("interdit")));
        Assert.Equal(Path.Combine(_root, "a", "sous", "deux.bin"), tree.GetPath(Node("deux.bin")));
        Assert.NotNull(tree.Modified(Node("deux.bin")));

        // Même contenu que l'analyse standard, au dossier interdit et au lien physique près.
        var standard = new StandardScanEngine().Scan(_root, new ScanProgress(), CancellationToken.None);
        Assert.Equal(standard.Size(FileTree.Root) - 200 + 300, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Directory_dates_are_the_directory_own_dates_not_the_stale_copy_held_by_its_parent()
    {
        // Un dossier dont on vient de modifier le contenu : NTFS met à jour sa date tout de suite, mais la copie
        // rangée chez le parent (celle que renvoie l'énumération du parent) peut rester en retard.
        string folder = Path.Combine(_root, "a", "sous");
        for (int i = 0; i < 5; i++) File.WriteAllBytes(Path.Combine(folder, $"ajout{i}.bin"), new byte[1]);

        var tree = new StandardScanEngine().Scan(_root, new ScanProgress(), CancellationToken.None);

        foreach (string name in new[] { "a", "sous" })
        {
            int node = Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == name);
            Assert.Equal(Directory.GetLastWriteTimeUtc(tree.GetPath(node)), tree.Modified(node));
        }
        int file = Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == "un.bin");
        Assert.Equal(File.GetLastWriteTimeUtc(tree.GetPath(file)), tree.Modified(file));
    }

    [Fact]
    public void Symbolic_links_were_really_part_of_the_fixture()
    {
        // Sans mode développeur ni élévation, les liens symboliques ne peuvent pas être créés :
        // le test principal ne couvre alors que la jonction. Ce test rend cette situation visible.
        Assert.True(_symlinksCreated, "Liens symboliques non créés (mode développeur ou administrateur requis) : cas non couvert sur cette machine.");
    }

    [Fact]
    public void Scan_handles_paths_longer_than_260_characters()
    {
        string deep = _root;
        for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string((char)('k' + i), 40));
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "profond.bin"), new byte[77]);
        Assert.True(deep.Length > 260);

        var tree = new StandardScanEngine().Scan(_root, new ScanProgress(), CancellationToken.None);
        Assert.Equal(1777, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Scan_of_a_single_empty_directory_returns_an_empty_tree()
    {
        string empty = Path.Combine(_root, "a", "vide");
        Directory.CreateDirectory(empty);
        var progress = new ScanProgress();
        var tree = new StandardScanEngine().Scan(empty, progress, CancellationToken.None);
        Assert.Equal(1, tree.Count);
        Assert.Equal(0, tree.Size(FileTree.Root));
        Assert.Equal(0, progress.Errors);
    }

    [Fact]
    public void Cancelled_scan_throws_instead_of_returning_a_partial_tree()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new StandardScanEngine().Scan(_root, new ScanProgress(), cancellation.Token));
    }
}
