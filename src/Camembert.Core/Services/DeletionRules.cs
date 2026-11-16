namespace Camembert.Core.Services;

/// <summary>
/// Emplacements que l'application refuse de supprimer. Règles reprises de la version Python :
/// le disque et tout élément à sa racine ; Windows, Program Files et ProgramData en entier ;
/// <c>Users</c>, chaque profil et ses éléments directs ; <c>AppData\Local</c>, <c>Roaming</c> et <c>LocalLow</c>.
/// S'y ajoutent les zones système que seule l'analyse rapide NTFS fait apparaître.
/// </summary>
public sealed class DeletionRules(IEnumerable<string> systemDirectories)
{
    static readonly string[] SystemRootFolders = ["system volume information", "$recycle.bin", "recovery"];

    readonly string[] _systemDirectories = systemDirectories
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Select(Normalize)
        .Distinct()
        .ToArray();

    /// <summary>Règles de cette machine, d'après les variables d'environnement de Windows.</summary>
    public static DeletionRules FromEnvironment() => new(
        new[] { "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData" }
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>());

    /// <summary>Raison du refus, en français, ou null si la suppression est permise.</summary>
    public string? BlockReason(string path)
    {
        // Un chemin incomplet (« C: », chemin relatif) dépendrait du dossier courant du processus : refusé.
        if (!Path.IsPathFullyQualified(path)) return "Chemin incomplet.";
        string full;
        try { full = Normalize(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Chemin invalide.";
        }
        if (full.StartsWith(@"\\")) return "Les emplacements réseau ne sont pas pris en charge.";

        string[] parts = full.Split('\\');
        if (parts.Length <= 2) return "Les éléments à la racine d'un disque sont protégés.";
        if (_systemDirectories.Any(s => full == s || full.StartsWith(s + "\\")))
            return "Dossier système de Windows ou des programmes installés.";
        if (parts[1].StartsWith('$') || SystemRootFolders.Contains(parts[1]))
            return "Zone réservée au système.";
        if (parts[1] == "users")
        {
            if (parts.Length <= 4) return "Profil utilisateur et ses dossiers directs (Documents, Bureau…).";
            if (parts[3] == "appdata" && parts.Length == 5) return "Dossier de données des applications (AppData).";
        }
        return null;
    }

    static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
