using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;
using Camembert.Core.Services;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using FileAttributes = System.IO.FileAttributes;

namespace Camembert.Windows.Preview;

/// <summary>Résultat d'un aperçu : toujours des métadonnées, plus une image, un texte ou un PDF selon le type.</summary>
public sealed class PreviewContent
{
    public List<KeyValuePair<string, string>> Details { get; } = [];
    public BitmapSource? Image { get; set; }
    public string? Text { get; set; }
    public PdfPages? Pdf { get; set; }
    /// <summary>Explication quand l'aperçu est limité ou impossible.</summary>
    public string? Note { get; set; }

    public void Add(string label, string value) => Details.Add(new(label, value));
}

/// <summary>Document PDF ouvert, rendu page par page à la demande par le composant PDF intégré à Windows.</summary>
public sealed class PdfPages(PdfDocument document)
{
    public int PageCount => (int)document.PageCount;

    public async Task<BitmapSource> RenderAsync(int pageIndex)
    {
        using var page = document.GetPage((uint)pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = PreviewLoader.MaxImageWidth });
        return await PreviewLoader.DecodeAsync(stream);
    }
}

/// <summary>
/// Aperçus chargés à la demande, hors du thread d'interface, avec des limites de taille. Lecture seule ;
/// un format inconnu ou un fichier illisible donne ses seules métadonnées et une note, jamais une exception.
/// </summary>
public static class PreviewLoader
{
    public const int MaxImageWidth = 1600;
    const long MaxImageBytes = 200L << 20;
    const int MaxTextBytes = 64 << 10;
    const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000;

    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg" };
    static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".toml", ".srt",
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".htm", ".css", ".java", ".c", ".cpp", ".h", ".hpp",
        ".rs", ".go", ".sql", ".sh", ".bat", ".cmd", ".ps1", ".csproj", ".sln", ".xaml", ".gitignore",
    };

    public static Task<PreviewContent> LoadAsync(string path, CancellationToken cancellation) =>
        Task.Run(() => LoadCoreAsync(path, cancellation), cancellation);

    static async Task<PreviewContent> LoadCoreAsync(string path, CancellationToken cancellation)
    {
        var content = new PreviewContent();
        content.Add("Nom", Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path);
        content.Add("Chemin", path);
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var directory = new DirectoryInfo(path);
                content.Add("Type", "Dossier");
                AddDates(content, directory);
                return content;
            }

            var file = new FileInfo(path);
            string extension = file.Extension;
            content.Add("Type", FileTypes.Describe(file.Name, false));
            content.Add("Taille sur le disque analysé", SizeFormat.Format(file.Length));
            AddDates(content, file);

            if ((attributes & CloudOnly) != 0)
                content.Note = "Fichier cloud non téléchargé : pas d'aperçu, pour ne pas déclencher son téléchargement.";
            else if (Images.Contains(extension))
                LoadImage(file, content);
            else if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                await LoadPdfAsync(path, content);
            else if (Videos.Contains(extension))
                await LoadVideoAsync(path, content);
            else if (Texts.Contains(extension) || extension.Length == 0)
                LoadText(file, content, mustBeText: extension.Length > 0);
            else
                content.Note = "Pas d'aperçu pour ce type de fichier.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                      or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Fichier verrouillé, protégé, corrompu, format non décodable… : métadonnées seules.
            content.Image = null;
            content.Text = null;
            content.Pdf = null;
            content.Note = "Aperçu impossible : " + ErrorText.Describe(e);
        }
        cancellation.ThrowIfCancellationRequested();
        return content;
    }

    static void AddDates(PreviewContent content, FileSystemInfo info)
    {
        content.Add("Créé le", info.CreationTime.ToString("dd/MM/yyyy HH:mm"));
        content.Add("Modifié le", info.LastWriteTime.ToString("dd/MM/yyyy HH:mm"));
    }

    static void LoadImage(FileInfo file, PreviewContent content)
    {
        if (file.Length > MaxImageBytes)
        {
            content.Note = $"Image trop volumineuse pour l'aperçu (plus de {SizeFormat.Format(MaxImageBytes)}).";
            return;
        }
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Premier passage : dimensions seulement. Second : décodage réduit, pour borner la mémoire.
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        content.Add("Dimensions", $"{width} × {height} pixels");
        stream.Position = 0;
        content.Image = Decode(stream, Math.Min(width, MaxImageWidth));
    }

    static BitmapSource Decode(Stream stream, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // tout lire maintenant : le fichier n'est pas gardé ouvert
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze(); // utilisable depuis le thread d'interface
        return image;
    }

    internal static async Task<BitmapSource> DecodeAsync(IRandomAccessStream winRtStream)
    {
        using var memory = new MemoryStream();
        winRtStream.Seek(0);
        await winRtStream.AsStreamForRead().CopyToAsync(memory);
        memory.Position = 0;
        return Decode(memory, 0);
    }

    static void LoadText(FileInfo file, PreviewContent content, bool mustBeText)
    {
        var buffer = new byte[(int)Math.Min(file.Length, MaxTextBytes)];
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            stream.ReadExactly(buffer);
        var (encoding, label, preamble) = DetectEncoding(buffer);
        if (encoding is null)
        {
            content.Note = mustBeText ? "Contenu binaire : pas d'aperçu en texte." : "Pas d'aperçu pour ce type de fichier.";
            return;
        }
        // Décodeur sans vidage final : un caractère coupé par la limite de lecture est simplement ignoré.
        var chars = new char[encoding.GetMaxCharCount(buffer.Length)];
        int count = encoding.GetDecoder().GetChars(buffer, preamble, buffer.Length - preamble, chars, 0, flush: false);
        content.Text = new string(chars, 0, count);
        content.Add("Encodage", label);
        if (file.Length > buffer.Length)
            content.Note = $"Aperçu limité aux {SizeFormat.Format(MaxTextBytes)} premiers.";
    }

    /// <summary>Marque d'ordre d'octets, sinon UTF-8 s'il est valide, sinon Latin-1 ; null pour un contenu binaire.</summary>
    internal static (Encoding? Encoding, string Label, int Preamble) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF])) return (Encoding.UTF8, "UTF-8 (avec BOM)", 3);
        if (bytes.StartsWith<byte>([0xFF, 0xFE])) return (Encoding.Unicode, "UTF-16 LE", 2);
        if (bytes.StartsWith<byte>([0xFE, 0xFF])) return (Encoding.BigEndianUnicode, "UTF-16 BE", 2);
        if (bytes.Contains((byte)0)) return (null, "", 0);
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            strict.GetDecoder().GetCharCount(bytes, flush: false);
            return (strict, "UTF-8", 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1, "ANSI (Latin-1)", 0);
        }
    }

    static async Task LoadPdfAsync(string path, PreviewContent content)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        var pages = new PdfPages(document);
        content.Add("Pages", pages.PageCount.ToString(CultureInfo.InvariantCulture));
        if (pages.PageCount == 0) return;
        content.Image = await pages.RenderAsync(0);
        content.Pdf = pages;
    }

    static async Task LoadVideoAsync(string path, PreviewContent content)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var video = await file.Properties.GetVideoPropertiesAsync();
        if (video.Duration > TimeSpan.Zero) content.Add("Durée", video.Duration.ToString(video.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
        if (video.Width > 0) content.Add("Dimensions", $"{video.Width} × {video.Height} pixels");
        if (video.Bitrate > 0) content.Add("Débit", $"{video.Bitrate / 1000:N0} kbit/s");
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 512);
        if (thumbnail is { Type: ThumbnailType.Image }) content.Image = await DecodeAsync(thumbnail);
        else content.Note = "Pas de miniature disponible pour cette vidéo.";
    }
}
