namespace Camembert.Core.Services;

/// <summary>
/// Catégorie de nettoyage recommandé : des emplacements dont le contenu se recrée tout seul ou ne sert plus.
/// Les chemins acceptent les variables d'environnement et « * » dans un segment. Un chemin qui désigne un dossier
/// vise son contenu (le dossier lui-même est gardé) ; un chemin qui désigne un fichier vise ce fichier.
/// </summary>
/// <param name="MinAge">Seuls les éléments non modifiés depuis au moins cette durée sont visés.</param>
/// <param name="ByWindows">Emplacement système, que seul Windows nettoie (Camembert n'y supprime rien) : on indique où faire.</param>
public sealed record CleanupCategory(string Id, string Name, string Description, string[] Paths, TimeSpan MinAge = default, string? ByWindows = null);

/// <summary>Élément visé par un nettoyage, mesuré au moment de la recherche.</summary>
public sealed record CleanupItem(string Path, bool IsDirectory, long Size, long ModifiedTicks);

public static class CleanupCatalog
{
    const string StorageSettings = "Paramètres Windows › Système › Stockage › Fichiers temporaires";

    /// <summary>Catégories reprises de la version Python.</summary>
    public static readonly CleanupCategory[] Categories =
    [
        new("temp", "Fichiers temporaires",
            "Restes laissés par les applications et les installeurs. Seuls les éléments non modifiés depuis plus de 24 h sont concernés.",
            [@"%TEMP%"], TimeSpan.FromHours(24)),
        new("browsers", "Caches des navigateurs",
            "Chrome, Edge, Brave, Firefox. Recréés automatiquement. Mots de passe, historique et favoris ne sont pas touchés. Fermez les navigateurs avant : les fichiers qu'ils utilisent sont laissés en place.",
            [
                @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache\Cache_Data",
                @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Code Cache\*",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Cache\Cache_Data",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Code Cache\*",
                @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Cache\Cache_Data",
                @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Code Cache\*",
                @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2\entries",
            ]),
        new("dumps", "Rapports de plantage",
            "Images mémoire créées lors de plantages d'applications. Utiles seulement pour un diagnostic.",
            [@"%LOCALAPPDATA%\CrashDumps"]),
        new("dev", "Caches développeur (npm, pip)",
            "Paquets retéléchargés au prochain « install ». Aucun projet n'est touché.",
            [@"%LOCALAPPDATA%\npm-cache", @"%LOCALAPPDATA%\pip\Cache"]),
        new("system", "Fichiers temporaires de Windows",
            "Dossier Temp de Windows, mises à jour déjà installées, rapports de plantage du système. Taille minimale : une partie de ces dossiers n'est pas lisible sans droits d'administrateur.",
            [@"%SystemRoot%\Temp", @"%SystemRoot%\SoftwareDistribution\Download", @"%SystemRoot%\Minidump", @"%SystemRoot%\MEMORY.DMP"],
            ByWindows: StorageSettings),
    ];

    /// <summary>Éléments visés par une catégorie, avec leur taille. Les liens ne sont ni suivis ni comptés.</summary>
    public static List<CleanupItem> FindItems(CleanupCategory category, DateTime nowUtc, CancellationToken cancellation = default)
    {
        var items = new List<CleanupItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in category.Paths)
        {
            foreach (string match in Expand(Environment.ExpandEnvironmentVariables(pattern)))
            {
                cancellation.ThrowIfCancellationRequested();
                FileSystemInfo target = new DirectoryInfo(match);
                IEnumerable<FileSystemInfo> candidates;
                if (target.Exists && target.LinkTarget is null) candidates = Children((DirectoryInfo)target);
                else if (File.Exists(match)) candidates = [new FileInfo(match)];
                else continue;
                foreach (var info in candidates)
                {
                    if (info.LinkTarget is not null || !seen.Add(info.FullName)) continue;
                    if (category.MinAge > TimeSpan.Zero && nowUtc - info.LastWriteTimeUtc < category.MinAge) continue;
                    bool isDirectory = info is DirectoryInfo;
                    long size = isDirectory ? DirectorySize((DirectoryInfo)info, cancellation) : ((FileInfo)info).Length;
                    items.Add(new CleanupItem(info.FullName, isDirectory, size, info.LastWriteTimeUtc.ToFileTimeUtc()));
                }
            }
        }
        return items;
    }

    /// <summary>Taille d'un dossier, sans suivre les liens ; les sous-dossiers illisibles comptent pour 0.</summary>
    public static long DirectorySize(DirectoryInfo directory, CancellationToken cancellation = default)
    {
        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // ni jonctions ni liens symboliques
        };
        try
        {
            foreach (var file in directory.EnumerateFiles("*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return total;
    }

    static IEnumerable<FileSystemInfo> Children(DirectoryInfo directory)
    {
        try { return directory.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Chemins existants correspondant à un modèle où « * » peut remplacer un segment entier ou une partie.</summary>
    internal static IEnumerable<string> Expand(string pattern)
    {
        string[] segments = pattern.Split('\\');
        IEnumerable<string> current = [segments[0] + "\\"];
        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool last = i == segments.Length - 1;
            current = current.SelectMany(parent =>
            {
                if (!segment.Contains('*')) return [Path.Combine(parent, segment)];
                try
                {
                    var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    return last ? Directory.EnumerateFileSystemEntries(parent, segment, options).ToList()
                                : Directory.EnumerateDirectories(parent, segment, options).ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
            });
        }
        return current.Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
    }
}
