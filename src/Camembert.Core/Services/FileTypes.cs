namespace Camembert.Core.Services;

public enum FileCategory { Images, Videos, Audio, Documents, Archives, Code, Programs }

public static class FileTypes
{
    static readonly Dictionary<FileCategory, (string Label, string[] Extensions)> Categories = new()
    {
        [FileCategory.Images] = ("Images", [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".svg", ".ico", ".raw", ".psd"]),
        [FileCategory.Videos] = ("Vidéos", [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv"]),
        [FileCategory.Audio] = ("Audio", [".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus"]),
        [FileCategory.Documents] = ("Documents", [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".txt", ".rtf", ".md", ".epub", ".csv"]),
        [FileCategory.Archives] = ("Archives et images disque", [".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".vhd", ".vhdx"]),
        [FileCategory.Code] = ("Code source", [".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".c", ".cpp", ".h", ".hpp", ".rs", ".go", ".html", ".css", ".json", ".xml", ".yml", ".yaml", ".sql", ".sh", ".ps1"]),
        [FileCategory.Programs] = ("Programmes", [".exe", ".dll", ".msi", ".sys", ".bat", ".cmd", ".com", ".appx", ".msix"]),
    };

    public static string Label(FileCategory category) => Categories[category].Label;

    /// <summary>Extensions de la catégorie, en minuscules avec le point.</summary>
    public static string[] Extensions(FileCategory category) => Categories[category].Extensions;

    /// <summary>Libellé de la colonne « Type ».</summary>
    public static string Describe(string name, bool isDirectory)
    {
        if (isDirectory) return "Dossier";
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && name.Length - dot <= 12 ? "Fichier " + name[(dot + 1)..].ToUpperInvariant() : "Fichier";
    }
}
