using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camembert.Windows.Preview;

namespace Camembert.Windows.Tests;

public sealed class PreviewLoaderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "CamembertTests_" + Guid.NewGuid().ToString("N"));

    public PreviewLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static string Detail(PreviewContent content, string label) => content.Details.Single(d => d.Key == label).Value;

    [Fact]
    public async Task Png_image_is_decoded_with_its_dimensions_and_bounded_in_size()
    {
        var pixels = new byte[3000 * 20 * 4];
        var bitmap = BitmapSource.Create(3000, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 3000 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(_root, "large.png");
        using (var stream = File.Create(path)) encoder.Save(stream);

        var content = await PreviewLoader.LoadAsync(path, CancellationToken.None);

        Assert.Equal("3000 × 20 pixels", Detail(content, "Dimensions"));
        Assert.NotNull(content.Image);
        Assert.Equal(PreviewLoader.MaxImageWidth, content.Image.PixelWidth); // réduite au décodage
        Assert.True(content.Image.IsFrozen);
        File.Delete(path); // le fichier n'est pas resté ouvert
    }

    [Theory]
    [InlineData("utf8.txt", "UTF-8")]
    [InlineData("bom.txt", "UTF-8 (avec BOM)")]
    [InlineData("utf16.txt", "UTF-16 LE")]
    [InlineData("ansi.txt", "ANSI (Latin-1)")]
    public async Task Text_encoding_is_detected(string name, string expectedEncoding)
    {
        const string text = "Été à Besançon : ça coûte 12 €uros ?";
        const string latin = "Été à Besançon : ça coûte 12 euros ?";
        byte[] bytes = name switch
        {
            "utf8.txt" => new UTF8Encoding(false).GetBytes(text),
            "bom.txt" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf16.txt" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            _ => Encoding.Latin1.GetBytes(latin),
        };
        var content = await PreviewLoader.LoadAsync(Write(name, bytes), CancellationToken.None);
        Assert.Equal(name == "ansi.txt" ? latin : text, content.Text);
        Assert.Equal(expectedEncoding, Detail(content, "Encodage"));
        Assert.Null(content.Note);
    }

    [Fact]
    public async Task Long_text_is_truncated_without_breaking_a_character()
    {
        // 100 000 « é » : 2 octets chacun, la limite de 64 Ko tombe exactement entre deux caractères ou au milieu d'un.
        string path = Write("long.log", Encoding.UTF8.GetBytes("x" + new string('é', 100_000)));
        var content = await PreviewLoader.LoadAsync(path, CancellationToken.None);
        Assert.NotNull(content.Text);
        Assert.InRange(content.Text.Length, 32_000, 33_000);
        Assert.DoesNotContain('�', content.Text);
        Assert.Contains("limité", content.Note);
    }

    [Fact]
    public async Task Binary_content_with_a_text_extension_is_not_shown_as_text()
    {
        var content = await PreviewLoader.LoadAsync(Write("faux.txt", [1, 2, 0, 3, 0, 0, 200]), CancellationToken.None);
        Assert.Null(content.Text);
        Assert.Contains("binaire", content.Note);
    }

    [Fact]
    public async Task Unknown_format_corrupted_image_and_missing_file_give_metadata_and_a_note()
    {
        var unknown = await PreviewLoader.LoadAsync(Write("données.xyz", new byte[50]), CancellationToken.None);
        Assert.Equal("données.xyz", Detail(unknown, "Nom"));
        Assert.Equal("50 octets", Detail(unknown, "Taille sur le disque analysé"));
        Assert.Contains("Pas d'aperçu", unknown.Note);

        var corrupted = await PreviewLoader.LoadAsync(Write("cassée.jpg", Encoding.ASCII.GetBytes("ceci n'est pas une image")), CancellationToken.None);
        Assert.Null(corrupted.Image);
        Assert.StartsWith("Aperçu impossible", corrupted.Note);

        var missing = await PreviewLoader.LoadAsync(Path.Combine(_root, "absent.txt"), CancellationToken.None);
        Assert.StartsWith("Aperçu impossible", missing.Note);

        var corruptedPdf = await PreviewLoader.LoadAsync(Write("cassé.pdf", Encoding.ASCII.GetBytes("pas un pdf")), CancellationToken.None);
        Assert.Null(corruptedPdf.Pdf);
        Assert.StartsWith("Aperçu impossible", corruptedPdf.Note);
    }

    [Fact]
    public async Task Pdf_pages_are_rendered_by_the_built_in_component()
    {
        // PDF minimal de deux pages vides (200 × 100 points), écrit à la main avec sa table de références.
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Object(string body) { offsets.Add(pdf.Length); pdf.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>");
        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var content = await PreviewLoader.LoadAsync(Write("deux pages.pdf", Encoding.ASCII.GetBytes(pdf.ToString())), CancellationToken.None);

        Assert.Null(content.Note);
        Assert.Equal("2", Detail(content, "Pages"));
        Assert.NotNull(content.Pdf);
        Assert.NotNull(content.Image);
        Assert.Equal(2.0, (double)content.Image.PixelWidth / content.Image.PixelHeight, 1);
        var second = await content.Pdf.RenderAsync(1);
        Assert.Equal(PreviewLoader.MaxImageWidth, second.PixelWidth);
    }

    [Fact]
    public async Task Directory_preview_gives_its_metadata()
    {
        var content = await PreviewLoader.LoadAsync(_root, CancellationToken.None);
        Assert.Equal("Dossier", Detail(content, "Type"));
        Assert.Null(content.Image);
    }
}
