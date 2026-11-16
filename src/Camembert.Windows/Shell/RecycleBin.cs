using System.Runtime.InteropServices;
using System.Security.Principal;
using Camembert.Core.Services;

namespace Camembert.Windows.Shell;

/// <summary>Corbeille de l'utilisateur, sur tous les disques fixes.</summary>
public static class RecycleBin
{
    /// <summary>
    /// Taille et nombre d'éléments, en lisant directement les dossiers <c>$Recycle.Bin\&lt;SID&gt;</c> : rapide, quand
    /// l'API Windows équivalente (SHQueryRecycleBin) peut mettre plusieurs minutes. Chaque élément supprimé y est
    /// une paire « $I… » (sa fiche) et « $R… » (son contenu).
    /// </summary>
    public static (long Size, int Count) Measure()
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        long size = 0;
        int count = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var bin = new DirectoryInfo(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid));
                if (!bin.Exists) continue;
                foreach (var entry in bin.EnumerateFileSystemInfos("$R*"))
                {
                    count++;
                    size += entry is FileInfo file ? file.Length : CleanupCatalog.DirectorySize((DirectoryInfo)entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (size, count);
    }

    /// <summary>
    /// Vide la Corbeille de tous les disques. Définitif. À n'appeler qu'après une confirmation explicite.
    /// Renvoie null en cas de succès, sinon la raison de l'échec.
    /// </summary>
    public static Task<string?> EmptyAsync(IntPtr owner)
    {
        var completion = new TaskCompletionSource<string?>();
        // API Shell : thread STA, hors du thread d'interface (peut durer longtemps).
        var thread = new Thread(() =>
        {
            int hr = SHEmptyRecycleBinW(owner, null, NoConfirmation | NoProgressUi | NoSound);
            // S_OK, ou « vide » (0x8000FFFF) quand il n'y avait rien.
            completion.SetResult(hr >= 0 || unchecked((uint)hr) == 0x8000FFFF ? null : $"Windows n'a pas pu vider la Corbeille (code 0x{hr:X8}).");
        }) { IsBackground = true, Name = "Camembert vider la Corbeille" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBinW(IntPtr owner, string? rootPath, uint flags);
}
