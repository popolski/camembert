using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;

namespace Camembert.Scanner.Ntfs;

/// <summary>
/// Contenu utile de la MFT, un emplacement par numéro d'enregistrement, puis construction de l'arbre.
/// Les données viennent du disque brut : chaque décalage est vérifié avant lecture, un enregistrement
/// incohérent est ignoré.
/// </summary>
internal sealed class MftIndex
{
    public const int RootRecord = 5;
    const byte InUse = 1, IsDirectory = 2;   // _flags, écrits par l'enregistrement de base
    const byte Surrogate = 1, Cloud = 2;     // _reparse, écrits par l'attribut $REPARSE_POINT
    const uint StandardInformation = 0x10, FileName = 0x30, Data = 0x80, ReparsePoint = 0xC0, End = 0xFFFFFFFF;
    const ulong RecordMask = 0xFFFF_FFFF_FFFF; // référence = 48 bits de numéro + 16 bits de séquence

    readonly record struct Link(int Record, int Parent, int NameOffset, byte NameLength);

    readonly int[] _parent;       // -1 = pas de nom
    readonly int[] _nameOffset;
    readonly byte[] _nameLength;  // en caractères UTF-16
    readonly long[] _size, _allocated, _modified;
    readonly byte[] _flags, _reparse;
    char[] _names = new char[1 << 20];
    int _namesLength;
    readonly List<Link> _extraLinks = [];   // noms supplémentaires d'un même fichier (liens physiques)
    readonly List<Link> _deferred = [];     // noms trouvés dans un enregistrement d'extension

    public MftIndex(int recordCount)
    {
        _parent = new int[recordCount];
        Array.Fill(_parent, -1);
        _nameOffset = new int[recordCount];
        _nameLength = new byte[recordCount];
        _size = new long[recordCount];
        _allocated = new long[recordCount];
        _modified = new long[recordCount];
        _flags = new byte[recordCount];
        _reparse = new byte[recordCount];
    }

    public int RecordCount => _parent.Length;

    /// <summary>Analyse un enregistrement FILE (corrige ses séquences de mise à jour sur place). Renvoie la taille de données trouvée, pour la progression.</summary>
    public long ParseRecord(Span<byte> record, int recordNumber)
    {
        try
        {
            return ParseRecordCore(record, recordNumber);
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0; // attribut tronqué : enregistrement ignoré
        }
    }

    long ParseRecordCore(Span<byte> record, int recordNumber)
    {
        if ((uint)recordNumber >= (uint)_parent.Length || record.Length < 0x30 || !record[..4].SequenceEqual("FILE"u8)) return 0;
        if (!ApplyFixups(record)) return 0;
        ushort headerFlags = U16(record, 0x16);
        if ((headerFlags & 1) == 0) return 0; // enregistrement libre

        ulong baseReference = U64(record, 0x20);
        bool isBase = baseReference == 0;
        int target = recordNumber;
        if (isBase)
            _flags[recordNumber] = (byte)(InUse | ((headerFlags & 2) != 0 ? IsDirectory : 0));
        else
        {
            ulong baseRecord = baseReference & RecordMask;
            if (baseRecord >= (ulong)_parent.Length) return 0;
            target = (int)baseRecord;
        }

        long found = 0;
        int offset = U16(record, 0x14);
        int used = (int)Math.Min(U32(record, 0x18), (uint)record.Length);
        while (offset + 16 <= used)
        {
            uint type = U32(record, offset);
            if (type == End) break;
            uint length = U32(record, offset + 4);
            if (length < 16 || length > (uint)(used - offset)) break;
            var attribute = record.Slice(offset, (int)length);
            offset += (int)length;
            bool nonResident = attribute[8] != 0;

            switch (type)
            {
                case StandardInformation when !nonResident && Value(attribute) is { Length: >= 0x10 } value:
                    _modified[target] = (long)U64(value, 8);
                    break;

                case FileName when !nonResident && Value(attribute) is { Length: >= 0x42 } value:
                    int nameLength = value[0x40];
                    ulong parent = U64(value, 0) & RecordMask;
                    if (value[0x41] == 2 || nameLength == 0 || 0x42 + nameLength * 2 > value.Length || parent >= (ulong)_parent.Length)
                        break; // espace de noms 2 = nom court DOS, doublon du nom long
                    int nameOffset = AppendName(MemoryMarshal.Cast<byte, char>(value.Slice(0x42, nameLength * 2)));
                    if (isBase && _parent[recordNumber] < 0)
                    {
                        _parent[recordNumber] = (int)parent;
                        _nameOffset[recordNumber] = nameOffset;
                        _nameLength[recordNumber] = (byte)nameLength;
                    }
                    else
                        (isBase ? _extraLinks : _deferred).Add(new Link(target, (int)parent, nameOffset, (byte)nameLength));
                    break;

                case Data when attribute[9] == 0: // flux principal uniquement (sans nom)
                    if (!nonResident)
                    {
                        _size[target] = _allocated[target] = U32(attribute, 0x10);
                    }
                    else if (attribute.Length >= 0x40 && U64(attribute, 0x10) == 0) // premier fragment : il porte les tailles
                    {
                        bool hasTotalAllocated = (U16(attribute, 0x0C) & 0x8001) != 0 && U16(attribute, 0x20) >= 0x48 && attribute.Length >= 0x48;
                        _size[target] = Math.Max(0, (long)U64(attribute, 0x30));
                        _allocated[target] = Math.Max(0, (long)U64(attribute, hasTotalAllocated ? 0x40 : 0x28));
                        found = _size[target];
                    }
                    break;

                case ReparsePoint when !nonResident && Value(attribute) is { Length: >= 4 } value:
                    uint tag = U32(value, 0);
                    _reparse[target] = (byte)(((tag & 0x20000000) != 0 ? Surrogate : 0) | ((tag & 0xFFFF0FFF) == 0x9000001A ? Cloud : 0));
                    break;
            }
        }
        return found;
    }

    /// <summary>À appeler une fois tous les enregistrements lus : rattache les noms trouvés dans les enregistrements d'extension.</summary>
    public void Complete()
    {
        foreach (var link in _deferred)
        {
            if (_parent[link.Record] < 0)
            {
                _parent[link.Record] = link.Parent;
                _nameOffset[link.Record] = link.NameOffset;
                _nameLength[link.Record] = link.NameLength;
            }
            else
                _extraLinks.Add(link);
        }
        _deferred.Clear();
    }

    /// <summary>
    /// Construit l'arbre sous <paramref name="startRecord"/>, en largeur. Jonctions et liens symboliques sont exclus ;
    /// un fichier à plusieurs liens physiques apparaît sous chaque nom mais sa taille n'est comptée qu'une fois ;
    /// un fichier cloud ne compte que pour ce qui est réellement sur le disque.
    /// </summary>
    public FileTree BuildTree(string rootPath, int startRecord, ScanProgress progress, CancellationToken cancellation)
    {
        int n = _parent.Length;
        if ((uint)startRecord >= (uint)n || (_flags[startRecord] & (InUse | IsDirectory)) != (InUse | IsDirectory))
            throw new InvalidDataException("Le dossier à analyser est introuvable dans la MFT.");

        // Tri par comptage : les enfants de chaque dossier, regroupés. Un lien supplémentaire est codé ~indice.
        var end = new int[n + 1];
        for (int r = 0; r < n; r++)
            if (IsListed(r) && _parent[r] != r) end[_parent[r]]++;
        foreach (var link in _extraLinks)
            if (IsListed(link.Record)) end[link.Parent]++;
        int total = 0;
        for (int p = 0; p < n; p++) { int count = end[p]; end[p] = total; total += count; }
        end[n] = total;
        var children = new int[total];
        var next = (int[])end.Clone();
        for (int r = 0; r < n; r++)
            if (IsListed(r) && _parent[r] != r) children[next[_parent[r]]++] = r;
        for (int i = 0; i < _extraLinks.Count; i++)
            if (IsListed(_extraLinks[i].Record)) children[next[_extraLinks[i].Parent]++] = ~i;

        var tree = new FileTree(rootPath);
        var batch = new ChildBatch();
        var queue = new Queue<(int Record, int Node)>();
        queue.Enqueue((startRecord, FileTree.Root));
        long files = 0, directories = 1, bytes = 0;
        while (queue.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var (record, node) = queue.Dequeue();
            int from = end[record], to = end[record + 1];
            if (from == to) continue;
            batch.Clear();
            for (int i = from; i < to; i++)
            {
                int child = children[i];
                if (child < 0)
                {
                    var link = _extraLinks[~child];
                    batch.Add(_names.AsSpan(link.NameOffset, link.NameLength), 0, false, _modified[link.Record]);
                    continue;
                }
                bool isDirectory = (_flags[child] & IsDirectory) != 0;
                long size = isDirectory ? 0 : (_reparse[child] & Cloud) != 0 ? Math.Min(_size[child], _allocated[child]) : _size[child];
                batch.Add(_names.AsSpan(_nameOffset[child], _nameLength[child]), size, isDirectory, _modified[child]);
                if (isDirectory) directories++;
                else { files++; bytes += size; }
            }
            int first = tree.AddChildren(node, batch);
            for (int i = from; i < to; i++)
                if (children[i] >= 0 && (_flags[children[i]] & IsDirectory) != 0)
                    queue.Enqueue((children[i], first + (i - from)));
        }
        Interlocked.Exchange(ref progress.Files, files);
        Interlocked.Exchange(ref progress.Directories, directories);
        Interlocked.Exchange(ref progress.Bytes, bytes);
        return tree;
    }

    bool IsListed(int record) =>
        (_flags[record] & InUse) != 0 && _parent[record] >= 0 && (_reparse[record] & Surrogate) == 0;

    int AppendName(ReadOnlySpan<char> name)
    {
        if (_namesLength + name.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesLength + name.Length));
        name.CopyTo(_names.AsSpan(_namesLength));
        int offset = _namesLength;
        _namesLength += name.Length;
        return offset;
    }

    /// <summary>
    /// Chaque secteur d'un enregistrement se termine par un numéro de séquence, la vraie valeur étant
    /// rangée dans l'en-tête. Les remettre en place ; un numéro inattendu signale une écriture incomplète.
    /// </summary>
    internal static bool ApplyFixups(Span<byte> record)
    {
        int arrayOffset = U16(record, 4), count = U16(record, 6);
        if (count < 2 || arrayOffset + count * 2 > record.Length || record.Length % (count - 1) != 0) return false;
        int sector = record.Length / (count - 1);
        if (sector < 2) return false;
        ushort expected = U16(record, arrayOffset);
        for (int i = 1; i < count; i++)
        {
            var tail = record.Slice(i * sector - 2, 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tail) != expected) return false;
            record.Slice(arrayOffset + i * 2, 2).CopyTo(tail);
        }
        return true;
    }

    /// <summary>Valeur d'un attribut résident, ou vide si ses bornes sortent de l'attribut.</summary>
    internal static ReadOnlySpan<byte> Value(ReadOnlySpan<byte> attribute)
    {
        if (attribute.Length < 0x18) return default;
        uint length = U32(attribute, 0x10);
        int offset = U16(attribute, 0x14);
        return (ulong)offset + length <= (ulong)attribute.Length ? attribute.Slice(offset, (int)length) : default;
    }

    /// <summary>Décode une liste de fragments (data runs) en couples (premier cluster, nombre de clusters). Les trous sont ignorés.</summary>
    internal static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        int i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            int lengthSize = runs[i] & 0xF, offsetSize = runs[i] >> 4;
            i++;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || i + lengthSize + offsetSize > runs.Length) break;
            long clusters = 0;
            for (int b = 0; b < lengthSize; b++) clusters |= (long)runs[i + b] << (8 * b);
            i += lengthSize;
            if (offsetSize == 0) continue; // trou (fichier creux)
            long delta = 0;
            for (int b = 0; b < offsetSize; b++) delta |= (long)runs[i + b] << (8 * b);
            if (offsetSize < 8 && (runs[i + offsetSize - 1] & 0x80) != 0) delta |= -1L << (8 * offsetSize); // signé
            i += offsetSize;
            lcn += delta;
            if (clusters <= 0 || lcn < 0) break;
            result.Add((lcn, clusters));
        }
        return result;
    }

    internal static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o, 2));
    internal static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o, 4));
    internal static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(o, 8));
}
