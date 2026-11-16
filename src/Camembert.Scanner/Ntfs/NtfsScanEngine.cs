using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;
using Microsoft.Win32.SafeHandles;
using Camembert.Core.Services;

namespace Camembert.Scanner.Ntfs;

/// <summary>
/// Analyse rapide NTFS : lit la table de fichiers maîtresse (MFT) du volume d'un seul trait, sans parcourir les dossiers.
/// Exige des droits d'administrateur (ouverture du volume brut) ; lecture seule.
/// Lève <see cref="UnauthorizedAccessException"/> sans ces droits et <see cref="NotSupportedException"/> si le volume
/// n'est pas NTFS ou a une géométrie non gérée : l'appelant se replie alors sur l'analyse standard.
/// </summary>
public sealed class NtfsScanEngine : IScanEngine
{
    const int ChunkBytes = 4 << 20;

    public string Name => "analyse rapide NTFS (MFT)";

    /// <summary>Vrai si <paramref name="path"/> est sur un volume local NTFS.</summary>
    public static bool Supports(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady && drive.DriveFormat == "NTFS";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public FileTree Scan(string rootPath, ScanProgress progress, CancellationToken cancellation)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Supports(rootPath)) throw new NotSupportedException("Ce volume n'est pas un volume NTFS local.");
        string volume = Path.GetPathRoot(rootPath)!.TrimEnd('\\'); // « C: »
        bool wholeVolume = rootPath.TrimEnd('\\').Equals(volume, StringComparison.OrdinalIgnoreCase);
        int startRecord = wholeVolume ? MftIndex.RootRecord : RecordNumberOf(rootPath);

        // À la première lecture brute, NTFS écrit d'abord sur le disque ses données en attente : le résultat est à jour,
        // mais cette étape peut prendre quelques secondes sur un disque très sollicité.
        using var handle = CreateFileW(@"\\.\" + volume, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 5) throw new UnauthorizedAccessException("La lecture de la MFT exige des droits d'administrateur.");
            throw new Win32Exception(error);
        }

        var data = new NtfsVolumeData();
        if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, ref data, Marshal.SizeOf<NtfsVolumeData>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        int recordSize = (int)data.BytesPerFileRecordSegment, clusterSize = (int)data.BytesPerCluster;
        if (recordSize is not (1024 or 4096) || clusterSize < recordSize || ChunkBytes % clusterSize != 0)
            throw new NotSupportedException($"Géométrie NTFS non gérée (enregistrement {recordSize} octets, cluster {clusterSize} octets).");
        long recordCount = data.MftValidDataLength / recordSize;
        if (recordCount is < 16 or > int.MaxValue) throw new NotSupportedException("Taille de MFT non gérée.");

        progress.CurrentPath = "Lecture de la table de fichiers (MFT)…";
        var runs = ReadMftRuns(handle, data.MftStartLcn * clusterSize, recordSize, clusterSize);
        var index = new MftIndex((int)recordCount);

        // Un thread lit le volume par blocs de 4 Mo, celui-ci décode (moins d'une seconde pour 5 millions
        // d'enregistrements) : la durée est celle de la lecture du disque. Mesuré : quatre lecteurs en parallèle
        // n'apportent rien de mesurable, d'où un seul.
        using var chunks = new BlockingCollection<(byte[] Buffer, int Length, int FirstRecord)>(4);
        using var free = new BlockingCollection<byte[]>();
        for (int i = 0; i < 5; i++) free.Add(GC.AllocateUninitializedArray<byte>(ChunkBytes));
        Exception? readError = null;
        var reader = new Thread(() =>
        {
            try
            {
                long record = 0;
                foreach (var (lcn, clusters) in runs)
                {
                    long offset = lcn * clusterSize, remaining = Math.Min(clusters * clusterSize, (recordCount - record) * recordSize);
                    while (remaining > 0 && !cancellation.IsCancellationRequested)
                    {
                        var buffer = free.Take();
                        int length = (int)Math.Min(remaining, ChunkBytes);
                        // Lecture sur volume brut : positions et longueurs multiples du secteur.
                        int aligned = (length + clusterSize - 1) / clusterSize * clusterSize;
                        if (RandomAccess.Read(handle, buffer.AsSpan(0, aligned), offset) < length)
                            throw new IOException("Lecture incomplète de la MFT.");
                        chunks.Add((buffer, length, (int)record));
                        offset += length;
                        remaining -= length;
                        record += length / recordSize;
                    }
                }
            }
            catch (Exception e)
            {
                readError = e;
            }
            finally
            {
                chunks.CompleteAdding();
            }
        }) { IsBackground = true, Name = "Camembert lecture MFT" };
        reader.Start();

        try
        {
            foreach (var (buffer, length, firstRecord) in chunks.GetConsumingEnumerable())
            {
                if (cancellation.IsCancellationRequested) break;
                long files = 0, bytes = 0;
                for (int position = 0; position + recordSize <= length; position += recordSize)
                {
                    long size = index.ParseRecord(buffer.AsSpan(position, recordSize), firstRecord + position / recordSize);
                    if (size > 0) { files++; bytes += size; }
                }
                Interlocked.Add(ref progress.Files, files);
                Interlocked.Add(ref progress.Bytes, bytes);
                free.Add(buffer);
            }
        }
        finally
        {
            // Débloque le lecteur s'il attend un tampon, puis attend sa fin avant de fermer le volume.
            while (reader.IsAlive)
            {
                while (chunks.TryTake(out var pending)) free.Add(pending.Buffer);
                reader.Join(10);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        if (readError is not null) throw new IOException("Échec de la lecture de la MFT : " + ErrorText.Describe(readError), readError);

        progress.CurrentPath = "Construction de l'arborescence…";
        index.Complete();
        var tree = index.BuildTree(rootPath, startRecord, progress, cancellation);
        tree.Aggregate();
        return tree;
    }

    /// <summary>
    /// Emplacements de la MFT sur le volume, lus dans son propre premier enregistrement ($MFT). Si la liste des
    /// fragments déborde dans d'autres enregistrements ($ATTRIBUTE_LIST), ceux-ci sont lus à leur tour.
    /// </summary>
    static List<(long Lcn, long Clusters)> ReadMftRuns(SafeFileHandle volume, long mftOffset, int recordSize, int clusterSize)
    {
        var buffer = new byte[Math.Max(recordSize, clusterSize)];
        var record = ReadRecordAt(mftOffset);
        var fragments = new SortedDictionary<ulong, List<(long, long)>>(); // par premier VCN
        byte[]? attributeList = null;
        CollectDataRuns(record, fragments);
        ForEachAttribute(record, (type, attribute) =>
        {
            if (type != 0x20) return;
            if (attribute[8] == 0) attributeList = MftIndex.Value(attribute).ToArray();
            else if (attribute.Length >= 0x40)
            {
                // Liste non résidente : lire ses clusters.
                long size = (long)MftIndex.U64(attribute, 0x30);
                if (size is <= 0 or > 1 << 24) throw new NotSupportedException("Liste d'attributs de la MFT trop grande.");
                attributeList = new byte[size];
                var raw = new byte[clusterSize];
                long done = 0;
                foreach (var (lcn, clusters) in MftIndex.DecodeRuns(attribute[MftIndex.U16(attribute, 0x20)..]))
                    for (long c = 0; c < clusters && done < size; c++)
                    {
                        ReadExactly(volume, raw, (lcn + c) * clusterSize);
                        int n = (int)Math.Min(clusterSize, size - done);
                        raw.AsSpan(0, n).CopyTo(attributeList.AsSpan((int)done));
                        done += n;
                    }
            }
        });

        if (attributeList is not null)
        {
            // Entrée : type (4), longueur (2), …, premier VCN (8, à 0x08), référence d'enregistrement (8, à 0x10).
            var seen = new HashSet<long> { 0 };
            for (int offset = 0; offset + 0x1A <= attributeList.Length;)
            {
                var entry = attributeList.AsSpan(offset);
                int length = MftIndex.U16(entry, 4);
                if (length < 0x1A) break;
                long extension = (long)(MftIndex.U64(entry, 0x10) & 0xFFFF_FFFF_FFFF);
                if (MftIndex.U32(entry, 0) == 0x80 && entry[6] == 0 && seen.Add(extension))
                {
                    // L'enregistrement d'extension se trouve dans les fragments déjà connus (NTFS le garantit pour $MFT).
                    long position = OffsetOfRecord(extension, fragments, recordSize, clusterSize);
                    CollectDataRuns(ReadRecordAt(position), fragments);
                }
                offset += length;
            }
        }

        var runs = fragments.Values.SelectMany(r => r).ToList();
        if (runs.Count == 0) throw new InvalidDataException("Emplacement de la MFT illisible.");
        return runs;

        byte[] ReadRecordAt(long offset)
        {
            ReadExactly(volume, buffer, offset);
            var copy = buffer.AsSpan(0, recordSize).ToArray();
            if (!copy.AsSpan(0, 4).SequenceEqual("FILE"u8) || !MftIndex.ApplyFixups(copy))
                throw new InvalidDataException("Enregistrement $MFT invalide.");
            return copy;
        }
    }

    static void CollectDataRuns(byte[] record, SortedDictionary<ulong, List<(long, long)>> fragments) =>
        ForEachAttribute(record, (type, attribute) =>
        {
            if (type == 0x80 && attribute[9] == 0 && attribute[8] != 0 && attribute.Length >= 0x40)
                fragments[MftIndex.U64(attribute, 0x10)] = MftIndex.DecodeRuns(attribute[MftIndex.U16(attribute, 0x20)..]);
        });

    delegate void AttributeVisitor(uint type, ReadOnlySpan<byte> attribute);

    static void ForEachAttribute(ReadOnlySpan<byte> record, AttributeVisitor visit)
    {
        int offset = MftIndex.U16(record, 0x14);
        int used = (int)Math.Min(MftIndex.U32(record, 0x18), (uint)record.Length);
        while (offset + 16 <= used)
        {
            uint type = MftIndex.U32(record, offset);
            uint length = MftIndex.U32(record, offset + 4);
            if (type == 0xFFFFFFFF || length < 16 || length > (uint)(used - offset)) break;
            visit(type, record.Slice(offset, (int)length));
            offset += (int)length;
        }
    }

    static long OffsetOfRecord(long record, SortedDictionary<ulong, List<(long Lcn, long Clusters)>> fragments, int recordSize, int clusterSize)
    {
        long target = record * recordSize, position = 0;
        foreach (var (lcn, clusters) in fragments.Values.SelectMany(r => r))
        {
            long bytes = clusters * clusterSize;
            if (target < position + bytes) return lcn * clusterSize + (target - position);
            position += bytes;
        }
        throw new InvalidDataException("Fragment de MFT introuvable.");
    }

    static void ReadExactly(SafeFileHandle volume, byte[] buffer, long offset)
    {
        if (RandomAccess.Read(volume, buffer, offset) < buffer.Length) throw new IOException("Lecture incomplète du volume.");
    }

    /// <summary>Numéro d'enregistrement MFT d'un dossier (son identifiant de fichier sans le numéro de séquence).</summary>
    static int RecordNumberOf(string directory)
    {
        using var handle = CreateFileW(directory, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        ulong id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return checked((int)(id & 0xFFFF_FFFF_FFFF));
    }

    const uint GenericRead = 0x80000000, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
    const uint FsctlGetNtfsVolumeData = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    struct NtfsVolumeData
    {
        public long VolumeSerialNumber, NumberSectors, TotalClusters, FreeClusters, TotalReserved;
        public uint BytesPerSector, BytesPerCluster, BytesPerFileRecordSegment, ClustersPerFileRecordSegment;
        public long MftValidDataLength, MftStartLcn, Mft2StartLcn, MftZoneStart, MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, ref NtfsVolumeData output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
