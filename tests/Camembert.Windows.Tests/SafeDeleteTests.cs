using Camembert.Core.Services;
using Camembert.Windows.Shell;

namespace Camembert.Windows.Tests;

/// <summary>Fichiers réels dans un dossier temporaire. Le dossier temporaire est sous AppData\Local\Temp : non protégé.</summary>
public sealed class SafeDeleteTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "CamembertTests_" + Guid.NewGuid().ToString("N"));
    readonly DeletionRules _rules = DeletionRules.FromEnvironment();

    public SafeDeleteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string NewFile(string name, int size)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    /// <summary>Ce que l'analyse aurait enregistré pour cet élément à cet instant.</summary>
    static DeleteRequest Scanned(string path)
    {
        bool isDirectory = Directory.Exists(path);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return new DeleteRequest(1, path, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc.ToFileTimeUtc());
    }

    [Fact]
    public void Unchanged_file_and_folder_pass_the_check()
    {
        string file = NewFile("intact.bin", 100);
        string folder = Directory.CreateDirectory(Path.Combine(_root, "dossier")).FullName;
        Assert.Null(SafeDelete.Check(Scanned(file), _rules));
        Assert.Null(SafeDelete.Check(Scanned(folder), _rules));
    }

    [Fact]
    public void Missing_item_is_refused()
    {
        var request = Scanned(NewFile("parti.bin", 10));
        File.Delete(request.Path);
        Assert.Contains("Introuvable", SafeDelete.Check(request, _rules));
    }

    [Fact]
    public void File_modified_after_the_scan_is_refused()
    {
        var bigger = Scanned(NewFile("grossi.bin", 10));
        File.WriteAllBytes(bigger.Path, new byte[25]);
        Assert.Contains("Modifié depuis l'analyse", SafeDelete.Check(bigger, _rules));

        // Même taille mais contenu réécrit plus tard : la date suffit.
        var rewritten = Scanned(NewFile("réécrit.bin", 10));
        File.SetLastWriteTimeUtc(rewritten.Path, DateTime.UtcNow.AddMinutes(5));
        Assert.Contains("Modifié depuis l'analyse", SafeDelete.Check(rewritten, _rules));
    }

    [Fact]
    public void Folder_whose_content_changed_after_the_scan_is_refused()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "dossier")).FullName;
        Directory.SetLastWriteTimeUtc(folder, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var request = Scanned(folder);
        File.WriteAllBytes(Path.Combine(folder, "nouveau document important.docx"), new byte[10]); // ajouté après l'analyse
        Assert.Contains("Modifié depuis l'analyse", SafeDelete.Check(request, _rules));
    }

    [Fact]
    public void Item_replaced_by_another_kind_or_by_a_link_is_refused()
    {
        var wasFile = Scanned(NewFile("chose", 10));
        File.Delete(wasFile.Path);
        Directory.CreateDirectory(wasFile.Path);
        Assert.Contains("changé de nature", SafeDelete.Check(wasFile, _rules));

        string target = Directory.CreateDirectory(Path.Combine(_root, "cible")).FullName;
        string folder = Directory.CreateDirectory(Path.Combine(_root, "futur lien")).FullName;
        var wasFolder = Scanned(folder) with { ModifiedTicks = 0 };
        Directory.Delete(folder);
        try { Directory.CreateSymbolicLink(folder, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // ni mode développeur ni administrateur
        Assert.Contains("lien", SafeDelete.Check(wasFolder, _rules));
        Directory.Delete(folder);
    }

    [Fact]
    public void Protected_location_is_refused_before_anything_is_touched()
    {
        string system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        Assert.StartsWith("Emplacement protégé", SafeDelete.Check(Scanned(Path.Combine(system32, "kernel32.dll")), _rules));
        Assert.StartsWith("Emplacement protégé", SafeDelete.Check(Scanned(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), _rules));
    }

    [Fact]
    public async Task Refused_items_are_reported_and_left_on_disk()
    {
        // Aucun de ces éléments ne passe la vérification : rien n'atteint la Corbeille.
        var modified = Scanned(NewFile("modifié.bin", 10));
        File.WriteAllBytes(modified.Path, new byte[11]);
        var missing = Scanned(NewFile("absent.bin", 10));
        File.Delete(missing.Path);
        string kernel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");

        var results = await SafeDelete.SendToRecycleBinAsync([modified, missing, Scanned(kernel)], _rules, IntPtr.Zero);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => { Assert.False(r.Deleted); Assert.NotNull(r.Problem); });
        Assert.True(File.Exists(modified.Path));
        Assert.True(File.Exists(kernel));
    }

    /// <summary>
    /// Entrées de la Corbeille de l'utilisateur dont le chemin d'origine est sous le dossier de ce test.
    /// Chaque élément y est un couple « $Ixxxxxx » (fiche : contient le chemin d'origine) et « $Rxxxxxx » (le contenu).
    /// </summary>
    List<(string Info, string Content)> RecycledFromThisTest()
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string bin = Path.Combine(Path.GetPathRoot(_root)!, "$Recycle.Bin", sid);
        var found = new List<(string, string)>();
        foreach (string info in Directory.EnumerateFiles(bin, "$I*"))
        {
            if (File.GetLastWriteTimeUtc(info) < DateTime.UtcNow.AddMinutes(-10)) continue;
            string text = System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(info));
            if (text.Contains(_root, StringComparison.OrdinalIgnoreCase))
                found.Add((info, Path.Combine(bin, "$R" + Path.GetFileName(info)[2..])));
        }
        return found;
    }

    [Fact]
    public async Task Files_and_folders_really_go_to_the_recycle_bin()
    {
        string file = NewFile("CamembertTests fichier.bin", 1234);
        string folder = Directory.CreateDirectory(Path.Combine(_root, "CamembertTests dossier")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "dedans.bin"), new byte[10]);
        string locked = NewFile("verrouillé.bin", 10);
        string changed = NewFile("modifié.bin", 10);
        var changedRequest = Scanned(changed);
        File.WriteAllBytes(changed, new byte[99]);

        List<DeleteResult> results;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) // ouvert par « un autre programme »
            results = await SafeDelete.SendToRecycleBinAsync([Scanned(file), Scanned(folder), Scanned(locked), changedRequest], _rules, IntPtr.Zero);

        var recycled = RecycledFromThisTest();
        try
        {
            Assert.True(results[0].Deleted, results[0].Problem);
            Assert.True(results[1].Deleted, results[1].Problem);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(folder));
            // Dans la Corbeille, pas détruits : le contenu y est, récupérable.
            Assert.Equal(2, recycled.Count);
            Assert.Contains(recycled, r => File.Exists(r.Content) && new FileInfo(r.Content).Length == 1234);
            Assert.Contains(recycled, r => File.Exists(Path.Combine(r.Content, "dedans.bin")));

            Assert.False(results[2].Deleted);
            Assert.NotNull(results[2].Problem);
            Assert.True(File.Exists(locked));
            Assert.False(results[3].Deleted);
            Assert.True(File.Exists(changed));
        }
        finally
        {
            // Ne rien laisser dans la Corbeille de l'utilisateur : uniquement les entrées venues de ce test.
            foreach (var (info, content) in recycled)
            {
                if (Directory.Exists(content)) Directory.Delete(content, recursive: true);
                else File.Delete(content);
                File.Delete(info);
            }
        }
    }
}
