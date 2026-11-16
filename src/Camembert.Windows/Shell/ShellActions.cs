using System.Diagnostics;

namespace Camembert.Windows.Shell;

public static class ShellActions
{
    /// <summary>Ouvre l'Explorateur sur le dossier parent, l'élément sélectionné.</summary>
    public static void RevealInExplorer(string path)
    {
        // explorer.exe attend une ligne de commande brute : « /select,"chemin" » (un guillemet est interdit dans un nom Windows).
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }
}
