using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Camembert.Windows.Shell;

public static class KnownFolders
{
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Dossier Téléchargements réel (il peut avoir été déplacé, par exemple sur un autre disque).</summary>
    public static string Downloads()
    {
        if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0)
        {
            try { return Marshal.PtrToStringUni(pointer)!; }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Ouvre Paramètres Windows › Système › Stockage.</summary>
    public static void OpenStorageSettings()
    {
        using var _ = Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
}
