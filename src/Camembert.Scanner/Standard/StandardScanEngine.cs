using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;

namespace Camembert.Scanner.Standard;

/// <summary>
/// Analyse standard : énumération parallèle des dossiers par FindFirstFileEx (un appel système par bloc d'entrées,
/// aucun accès par fichier). Jonctions et liens symboliques ne sont ni suivis ni comptés ; les fichiers
/// cloud non téléchargés (OneDrive) comptent pour 0 octet ; un dossier inaccessible est marqué et l'analyse continue.
/// Limites connues : taille logique uniquement (pas l'espace alloué), liens physiques comptés une fois par nom.
/// </summary>
public sealed unsafe class StandardScanEngine : IScanEngine
{
    const uint Directory = 0x10, ReparsePoint = 0x400;
    const uint CloudOnly = 0x400000 | 0x40000 | 0x1000; // RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN | OFFLINE
    const uint NameSurrogate = 0x20000000;              // bit des balises « lien » : jonction, lien symbolique…
    const int ErrorFileNotFound = 2, ErrorNoMoreFiles = 18;
    static readonly IntPtr InvalidHandle = new(-1);

    public string Name => "analyse standard";

    public FileTree Scan(string rootPath, ScanProgress progress, CancellationToken cancellation)
    {
        rootPath = Path.GetFullPath(rootPath);
        var tree = new FileTree(rootPath);
        var todo = new ConcurrentStack<(int Node, string Path)>();
        todo.Push((FileTree.Root, rootPath));
        int pending = 1; // dossiers en file ou en cours ; 0 = analyse terminée
        Interlocked.Increment(ref progress.Directories);

        var threads = new Thread[Math.Clamp(Environment.ProcessorCount, 2, 16)];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(Worker) { IsBackground = true, Name = "Camembert analyse " + i };
            threads[i].Start();
        }
        foreach (var thread in threads) thread.Join();

        cancellation.ThrowIfCancellationRequested();
        tree.Aggregate();
        return tree;

        void Worker()
        {
            var batch = new ChildBatch();
            var subdirectories = new List<(int Index, string Name)>();
            var wait = new SpinWait();
            while (!cancellation.IsCancellationRequested)
            {
                if (!todo.TryPop(out var item))
                {
                    if (Volatile.Read(ref pending) == 0) return;
                    wait.SpinOnce();
                    continue;
                }
                wait.Reset();
                try
                {
                    progress.CurrentPath = item.Path;
                    batch.Clear();
                    subdirectories.Clear();
                    bool complete = ReadDirectory(item.Path, batch, subdirectories, progress, cancellation, out long ownModified);
                    // La date d'un dossier lue chez son parent peut être en retard (NTFS la recopie paresseusement) ;
                    // celle de son entrée « . » est la sienne propre. La suppression sécurisée s'appuie dessus.
                    if (ownModified != 0) tree.SetModifiedTicks(item.Node, ownModified);
                    if (!complete)
                    {
                        tree.MarkInaccessible(item.Node);
                        Interlocked.Increment(ref progress.Errors);
                    }
                    if (batch.Count == 0 || cancellation.IsCancellationRequested) continue;

                    int first = tree.AddChildren(item.Node, batch);
                    Interlocked.Add(ref pending, subdirectories.Count);
                    Interlocked.Add(ref progress.Directories, subdirectories.Count);
                    string prefix = item.Path.EndsWith('\\') ? item.Path : item.Path + "\\";
                    foreach (var (index, name) in subdirectories)
                        todo.Push((first + index, prefix + name));
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }
        }
    }

    /// <summary>Lit les entrées d'un dossier. Renvoie false si le dossier n'a pas pu être ouvert ou lu jusqu'au bout.</summary>
    /// <param name="ownModified">Date de modification du dossier lui-même (son entrée « . ») ; 0 si absente (racine d'un volume).</param>
    static bool ReadDirectory(string path, ChildBatch batch, List<(int, string)> subdirectories, ScanProgress progress, CancellationToken cancellation, out long ownModified)
    {
        ownModified = 0;
        WIN32_FIND_DATA data;
        IntPtr handle = FindFirstFileExW(ToSearchPattern(path), FindExInfoBasic, &data, 0, IntPtr.Zero, FindFirstExLargeFetch);
        if (handle == InvalidHandle)
            return Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorNoMoreFiles;

        long files = 0, bytes = 0;
        bool complete;
        try
        {
            do
            {
                var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data.cFileName);
                if (name is ".") ownModified = ((long)data.ftLastWriteHigh << 32) | data.ftLastWriteLow;
                if (name is "." or "..") continue;
                uint attributes = data.dwFileAttributes;
                if ((attributes & ReparsePoint) != 0 && (data.dwReserved0 & NameSurrogate) != 0)
                    continue; // lien : ne pas suivre ni compter, sinon boucles et double comptage

                long modified = ((long)data.ftLastWriteHigh << 32) | data.ftLastWriteLow;
                if ((attributes & Directory) != 0)
                {
                    subdirectories.Add((batch.Count, name.ToString()));
                    batch.Add(name, 0, isDirectory: true, modified);
                }
                else
                {
                    long size = (attributes & CloudOnly) != 0 ? 0 : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    batch.Add(name, size, isDirectory: false, modified);
                    bytes += size;
                    // Annulation rapide même dans un dossier de plusieurs centaines de milliers de fichiers.
                    if ((++files & 0xFFF) == 0 && cancellation.IsCancellationRequested) return true;
                }
            } while (FindNextFileW(handle, &data));
            complete = Marshal.GetLastPInvokeError() == ErrorNoMoreFiles;
        }
        finally
        {
            FindClose(handle);
        }
        Interlocked.Add(ref progress.Files, files);
        Interlocked.Add(ref progress.Bytes, bytes);
        return complete;
    }

    /// <summary>Préfixe « chemin long » pour dépasser MAX_PATH, puis joker.</summary>
    static string ToSearchPattern(string path)
    {
        string trimmed = path.TrimEnd('\\');
        if (trimmed.StartsWith(@"\\?\")) return trimmed + @"\*";
        if (trimmed.StartsWith(@"\\")) return @"\\?\UNC\" + trimmed[2..] + @"\*";
        return @"\\?\" + trimmed + @"\*";
    }

    const int FindExInfoBasic = 1, FindFirstExLargeFetch = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public uint ftCreationLow, ftCreationHigh, ftLastAccessLow, ftLastAccessHigh, ftLastWriteLow, ftLastWriteHigh;
        public uint nFileSizeHigh, nFileSizeLow;
        public uint dwReserved0; // balise de point d'analyse (reparse tag) si l'attribut est présent
        public uint dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, WIN32_FIND_DATA* data, int searchOp, IntPtr searchFilter, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindNextFileW(IntPtr handle, WIN32_FIND_DATA* data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(IntPtr handle);
}
