using System.Buffers.Binary;
using System.Text;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;
using Camembert.Scanner.Ntfs;

namespace Camembert.Scanner.Tests;

/// <summary>Décodage de la MFT sur des enregistrements FILE fabriqués en mémoire (aucun droit particulier requis).</summary>
public class MftIndexTests
{
    const int RecordSize = 1024;

    // ------------------------------------------------------------ fabrication d'enregistrements

    static byte[] Record(bool directory, params byte[][] attributes) => Record(directory, baseRecord: 0, inUse: true, attributes);

    static byte[] Record(bool directory, int baseRecord, bool inUse, params byte[][] attributes)
    {
        var r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        W16(r, 4, 0x30);  // tableau des séquences de mise à jour
        W16(r, 6, 3);     // numéro de séquence + 2 secteurs
        W16(r, 0x14, 0x38);
        W16(r, 0x16, (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        W64(r, 0x20, baseRecord == 0 ? 0 : (uint)baseRecord | (1UL << 48));
        int offset = 0x38;
        foreach (var attribute in attributes)
        {
            attribute.CopyTo(r, offset);
            offset += attribute.Length;
        }
        W32(r, offset, 0xFFFFFFFF);
        W32(r, 0x18, (uint)offset + 8);
        W32(r, 0x1C, RecordSize);
        // Protection : la fin de chaque secteur de 512 octets est remplacée par le numéro de séquence.
        W16(r, 0x30, 0xBEEF);
        for (int sector = 1; sector <= 2; sector++)
        {
            r.AsSpan(sector * 512 - 2, 2).CopyTo(r.AsSpan(0x30 + sector * 2));
            W16(r, sector * 512 - 2, 0xBEEF);
        }
        return r;
    }

    static byte[] Resident(uint type, byte[] value)
    {
        var a = new byte[(0x18 + value.Length + 7) & ~7];
        W32(a, 0, type);
        W32(a, 4, (uint)a.Length);
        W32(a, 0x10, (uint)value.Length);
        W16(a, 0x14, 0x18);
        value.CopyTo(a, 0x18);
        return a;
    }

    static byte[] StandardInformation(DateTime modified)
    {
        var v = new byte[0x30];
        W64(v, 8, (ulong)modified.ToFileTimeUtc());
        return Resident(0x10, v);
    }

    static byte[] FileName(int parent, string name, byte nameSpace = 1)
    {
        var v = new byte[0x42 + name.Length * 2];
        W64(v, 0, (uint)parent | (7UL << 48)); // numéro de séquence dans les 16 bits hauts : à ignorer
        v[0x40] = (byte)name.Length;
        v[0x41] = nameSpace;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 0x42);
        return Resident(0x30, v);
    }

    static byte[] ResidentData(int length) => Resident(0x80, new byte[length]);

    static byte[] NonResidentData(long size, long allocated, bool sparse = false, long startVcn = 0)
    {
        int header = sparse ? 0x48 : 0x40;
        var a = new byte[header + 8];
        W32(a, 0, 0x80);
        W32(a, 4, (uint)a.Length);
        a[8] = 1;
        W16(a, 0x0C, (ushort)(sparse ? 0x8000 : 0));
        W64(a, 0x10, (ulong)startVcn);
        W16(a, 0x20, (ushort)header);
        W64(a, 0x28, (ulong)(sparse ? size : allocated)); // creux : la taille allouée « nominale » couvre tout le fichier
        W64(a, 0x30, (ulong)size);
        if (sparse) W64(a, 0x40, (ulong)allocated);
        return a;
    }

    static byte[] NamedStream(string name, int length)
    {
        // Flux de données alternatif : nom à 0x18, valeur ensuite.
        var a = new byte[(0x18 + name.Length * 2 + length + 7) & ~7];
        W32(a, 0, 0x80);
        W32(a, 4, (uint)a.Length);
        a[9] = (byte)name.Length;
        W16(a, 0x0A, 0x18);
        W32(a, 0x10, (uint)length);
        W16(a, 0x14, (ushort)(0x18 + name.Length * 2));
        return a;
    }

    static byte[] Reparse(uint tag)
    {
        var v = new byte[8];
        W32(v, 0, tag);
        return Resident(0xC0, v);
    }

    static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);

    static readonly DateTime Date = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

    static (FileTree Tree, ScanProgress Progress) Build(params (int Number, byte[] Record)[] records)
    {
        var index = new MftIndex(64);
        index.ParseRecord(Record(true, FileName(5, ".")), 5); // racine : son propre parent
        foreach (var (number, record) in records) index.ParseRecord(record, number);
        index.Complete();
        var progress = new ScanProgress();
        var tree = index.BuildTree(@"X:\", MftIndex.RootRecord, progress, CancellationToken.None);
        tree.Aggregate();
        return (tree, progress);
    }

    static int Find(FileTree tree, string name) => Enumerable.Range(1, tree.Count - 1).Single(i => tree.Name(i) == name);

    static string[] Names(FileTree tree, int node) =>
        Enumerable.Range(tree.FirstChild(node), tree.ChildCount(node)).Select(tree.Name).Order(StringComparer.Ordinal).ToArray();

    // ------------------------------------------------------------ tests

    [Fact]
    public void Builds_tree_with_sizes_names_and_dates()
    {
        var (tree, progress) = Build(
            (30, Record(true, StandardInformation(Date), FileName(5, "Données"))),
            (31, Record(false, StandardInformation(Date), FileName(30, "petit.txt"), ResidentData(100))),
            (32, Record(false, FileName(30, "gros.bin"), NonResidentData(5000, 8192))),
            (33, Record(false, FileName(5, "racine.bin"), NonResidentData(1, 4096))));

        Assert.Equal(5101, tree.Size(FileTree.Root));
        Assert.Equal(["Données", "racine.bin"], Names(tree, FileTree.Root));
        int folder = Find(tree, "Données");
        Assert.True(tree.IsDirectory(folder));
        Assert.Equal(5100, tree.Size(folder));
        Assert.Equal(Date, tree.Modified(folder));
        Assert.Equal(@"X:\Données\petit.txt", tree.GetPath(Find(tree, "petit.txt")));
        Assert.Equal(100, tree.Size(Find(tree, "petit.txt")));
        Assert.Equal((3, 2, 5101), (progress.Files, progress.Directories, progress.Bytes));
    }

    [Fact]
    public void Hard_link_is_listed_under_each_name_but_counted_once()
    {
        var (tree, progress) = Build(
            (30, Record(true, FileName(5, "a"))),
            (31, Record(false, FileName(5, "original.bin"), FileName(30, "lien.bin"), NonResidentData(700, 4096))));

        Assert.Equal(700, tree.Size(FileTree.Root));
        Assert.Equal(700, tree.Size(Find(tree, "original.bin")));
        Assert.Equal(0, tree.Size(Find(tree, "lien.bin")));
        Assert.Equal(@"X:\a\lien.bin", tree.GetPath(Find(tree, "lien.bin")));
        Assert.Equal(1, progress.Files);
    }

    [Fact]
    public void Short_dos_name_is_not_a_second_entry()
    {
        var (tree, _) = Build((31, Record(false, FileName(5, "LONGNO~1.TXT", nameSpace: 2), FileName(5, "long nom de fichier.txt"), ResidentData(10))));
        Assert.Equal(["long nom de fichier.txt"], Names(tree, FileTree.Root));
        Assert.Equal(10, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Junctions_and_symbolic_links_are_excluded_with_everything_below()
    {
        var (tree, _) = Build(
            (30, Record(true, FileName(5, "jonction"), Reparse(0xA0000003))),
            (31, Record(false, FileName(30, "sous la jonction.bin"), NonResidentData(900, 4096))),
            (32, Record(false, FileName(5, "lien symbolique"), Reparse(0xA000000C), ResidentData(0))),
            (33, Record(false, FileName(5, "normal.bin"), ResidentData(50))));

        Assert.Equal(["normal.bin"], Names(tree, FileTree.Root));
        Assert.Equal(50, tree.Size(FileTree.Root));
        Assert.Equal(2, tree.Count);
    }

    [Fact]
    public void Cloud_file_counts_only_what_is_on_disk()
    {
        var (tree, _) = Build(
            (31, Record(false, FileName(5, "non téléchargé.mp4"), NonResidentData(9000, 0, sparse: true), Reparse(0x9000701A))),
            (32, Record(false, FileName(5, "téléchargé.mp4"), NonResidentData(9000, 12288), Reparse(0x9000701A))),
            (33, Record(false, FileName(5, "creux ordinaire.bin"), NonResidentData(9000, 0, sparse: true))));

        Assert.Equal(0, tree.Size(Find(tree, "non téléchargé.mp4")));
        Assert.Equal(9000, tree.Size(Find(tree, "téléchargé.mp4")));
        Assert.Equal(9000, tree.Size(Find(tree, "creux ordinaire.bin"))); // taille logique, comme le moteur standard
    }

    [Fact]
    public void Data_size_stored_in_an_extension_record_goes_to_the_base_file()
    {
        var (tree, _) = Build(
            (40, Record(false, baseRecord: 31, inUse: true, NonResidentData(777, 4096))), // lu avant sa base
            (31, Record(false, FileName(5, "fragmenté.bin"))),
            (41, Record(false, baseRecord: 31, inUse: true, NonResidentData(123456, 4096, startVcn: 50)))); // fragment suivant : sans effet

        Assert.Equal(["fragmenté.bin"], Names(tree, FileTree.Root));
        Assert.Equal(777, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Alternate_data_streams_are_not_counted()
    {
        var (tree, _) = Build((31, Record(false, FileName(5, "fichier.txt"), ResidentData(20), NamedStream("Zone.Identifier", 40))));
        Assert.Equal(20, tree.Size(FileTree.Root));
    }

    [Fact]
    public void Free_torn_orphan_and_garbage_records_are_ignored()
    {
        var torn = Record(false, FileName(5, "déchiré.bin"), ResidentData(10));
        torn[510] ^= 0xFF; // fin de secteur différente du numéro de séquence : écriture incomplète
        var truncated = Record(false, FileName(5, "tronqué.bin"), ResidentData(10));
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0x38 + 4), 0x7FFFFFFF); // longueur d'attribut délirante
        var garbage = new byte[RecordSize];
        new Random(1).NextBytes(garbage);

        var (tree, _) = Build(
            (31, Record(false, baseRecord: 0, inUse: false, FileName(5, "supprimé.bin"), ResidentData(10))),
            (32, torn),
            (33, truncated),
            (34, garbage),
            (35, Record(false, FileName(50, "orphelin.bin"), ResidentData(10))), // parent inexistant
            (36, Record(false, FileName(5, "bon.bin"), ResidentData(10))));

        Assert.Equal(["bon.bin"], Names(tree, FileTree.Root));
    }

    [Fact]
    public void Subtree_can_be_built_from_any_directory_record()
    {
        var index = new MftIndex(64);
        index.ParseRecord(Record(true, FileName(5, ".")), 5);
        index.ParseRecord(Record(true, FileName(5, "a")), 30);
        index.ParseRecord(Record(false, FileName(30, "dedans.bin"), ResidentData(5)), 31);
        index.ParseRecord(Record(false, FileName(5, "dehors.bin"), ResidentData(7)), 32);
        index.Complete();
        var tree = index.BuildTree(@"X:\a", 30, new ScanProgress(), CancellationToken.None);
        tree.Aggregate();
        Assert.Equal(5, tree.Size(FileTree.Root));
        Assert.Equal(@"X:\a\dedans.bin", tree.GetPath(1));
        Assert.Throws<InvalidDataException>(() => index.BuildTree(@"X:\x", 32, new ScanProgress(), CancellationToken.None)); // un fichier
    }

    [Fact]
    public void Data_runs_are_decoded_with_signed_relative_offsets_and_holes()
    {
        // 0x18 clusters en 0x5634 ; trou de 0x10 ; 0x20 clusters 0x100 plus bas ; fin.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x01, 0x10, 0x21, 0x20, 0x00, 0xFF, 0x00, 0x99];
        Assert.Equal([(0x5634L, 0x18L), (0x5534L, 0x20L)], MftIndex.DecodeRuns(runs));
        Assert.Empty(MftIndex.DecodeRuns([0x21, 0x18])); // tronqué
    }
}
