using System.Text;

namespace Camembert.Core.Models;

/// <summary>
/// Arborescence compacte : un nœud = un indice dans des tableaux parallèles (35 octets + le nom en UTF-8).
/// Les enfants d'un dossier sont ajoutés en un seul bloc, donc contigus, et un parent a toujours
/// un identifiant plus petit que ses enfants : l'agrégation est une simple boucle inverse.
/// Les écritures (<see cref="AddChildren"/>, <see cref="MarkInaccessible"/>) sont sûres entre threads ;
/// les lectures se font une fois l'analyse terminée.
/// </summary>
public sealed class FileTree
{
    public const int Root = 0;
    const byte DirectoryFlag = 1, InaccessibleFlag = 2, RemovedFlag = 4;

    readonly object _lock = new();
    int[] _parent = new int[1024];
    int[] _firstChild = new int[1024];
    int[] _childCount = new int[1024];
    int[] _nameStart = new int[1024];
    ushort[] _nameLength = new ushort[1024];
    long[] _size = new long[1024];
    long[] _modified = new long[1024]; // FILETIME UTC ; 0 = inconnue
    byte[] _flags = new byte[1024];
    byte[] _names = new byte[16 * 1024];
    int _namesLength;
    int _count;

    /// <param name="rootPath">Chemin complet de la racine analysée (ex. <c>C:\</c>) ; c'est le nom du nœud 0.</param>
    public FileTree(string rootPath)
    {
        var batch = new ChildBatch();
        batch.Add(rootPath, 0, isDirectory: true);
        Append(-1, batch);
    }

    public int Count => _count;
    public int Parent(int id) => _parent[id];
    public long Size(int id) => _size[id];
    /// <summary>Date de modification (UTC), ou null si le moteur ne l'a pas fournie.</summary>
    public DateTime? Modified(int id) => _modified[id] > 0 ? DateTime.FromFileTimeUtc(_modified[id]) : null;
    public long ModifiedTicks(int id) => _modified[id];
    /// <summary>Met à jour la date de modification connue (après un changement fait par l'application elle-même).</summary>
    public void SetModifiedTicks(int id, long ticks)
    {
        lock (_lock) _modified[id] = ticks;
    }
    internal ReadOnlySpan<byte> NameBytes => _names.AsSpan(0, _namesLength);
    internal int NameStart(int id) => _nameStart[id];
    internal int NameLength(int id) => _nameLength[id];
    public bool IsDirectory(int id) => (_flags[id] & DirectoryFlag) != 0;
    public bool IsInaccessible(int id) => (_flags[id] & InaccessibleFlag) != 0;
    /// <summary>Élément supprimé du disque depuis l'analyse (voir <see cref="Remove"/>).</summary>
    public bool IsRemoved(int id) => (_flags[id] & RemovedFlag) != 0;
    public int FirstChild(int id) => _firstChild[id];
    public int ChildCount(int id) => _childCount[id];
    public string Name(int id) => Encoding.UTF8.GetString(_names, _nameStart[id], _nameLength[id]);

    /// <summary>Ajoute en un bloc tous les enfants d'un dossier. Renvoie l'identifiant du premier ; les suivants sont consécutifs.</summary>
    public int AddChildren(int parent, ChildBatch children)
    {
        lock (_lock)
        {
            if ((uint)parent >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(parent));
            if (_childCount[parent] != 0) throw new InvalidOperationException("Les enfants de ce dossier ont déjà été ajoutés.");
            int first = Append(parent, children);
            _firstChild[parent] = first;
            _childCount[parent] = children.Count;
            return first;
        }
    }

    public void MarkInaccessible(int id)
    {
        lock (_lock) _flags[id] |= InaccessibleFlag;
    }

    /// <summary>
    /// Calcule la taille de chaque dossier, des feuilles vers la racine, et rend la capacité inutilisée des tableaux.
    /// À appeler une fois la construction terminée ; peut être rappelée sans effet cumulatif.
    /// </summary>
    public void Aggregate()
    {
        lock (_lock)
        {
            Array.Resize(ref _parent, _count);
            Array.Resize(ref _firstChild, _count);
            Array.Resize(ref _childCount, _count);
            Array.Resize(ref _nameStart, _count);
            Array.Resize(ref _nameLength, _count);
            Array.Resize(ref _size, _count);
            Array.Resize(ref _modified, _count);
            Array.Resize(ref _flags, _count);
            Array.Resize(ref _names, _namesLength);
            for (int i = 0; i < _count; i++)
                if ((_flags[i] & DirectoryFlag) != 0) _size[i] = 0;
            for (int i = _count - 1; i > 0; i--)
                _size[_parent[i]] += _size[i];
        }
    }

    /// <summary>
    /// Retire un élément et tout ce qu'il contient après sa suppression du disque : il disparaît des listes et
    /// de la recherche, et sa taille est déduite de tous ses dossiers parents, sans nouvelle analyse.
    /// </summary>
    public void Remove(int id)
    {
        lock (_lock)
        {
            if (id <= Root || (uint)id >= (uint)_count || (_flags[id] & RemovedFlag) != 0) return;
            long size = _size[id];
            for (int i = _parent[id]; i >= Root; i = _parent[i]) _size[i] -= size;
            var pending = new Stack<int>();
            pending.Push(id);
            while (pending.Count > 0)
            {
                int node = pending.Pop();
                _flags[node] |= RemovedFlag;
                for (int i = 0; i < _childCount[node]; i++) pending.Push(_firstChild[node] + i);
            }
        }
    }

    /// <summary>Reconstruit le chemin complet à la demande en remontant les parents.</summary>
    public string GetPath(int id)
    {
        var parts = new Stack<string>();
        for (int i = id; i > Root; i = _parent[i]) parts.Push(Name(i));
        var path = new StringBuilder(Name(Root));
        foreach (var part in parts)
        {
            if (path.Length > 0 && path[^1] != '\\') path.Append('\\');
            path.Append(part);
        }
        return path.ToString();
    }

    /// <summary>Enfants directs encore présents, du plus gros au plus petit.</summary>
    public int[] ChildrenBySize(int id)
    {
        var ids = new List<int>(_childCount[id]);
        for (int i = 0; i < _childCount[id]; i++)
            if ((_flags[_firstChild[id] + i] & RemovedFlag) == 0) ids.Add(_firstChild[id] + i);
        var result = ids.ToArray();
        Array.Sort(Array.ConvertAll(result, child => -_size[child]), result);
        return result;
    }

    const uint Magic = 0x314D4143; // "CAM1"

    /// <summary>Écrit l'arbre tel quel (tableaux bruts). Sert à l'échange avec le processus d'analyse élevé.</summary>
    public void Write(Stream stream)
    {
        lock (_lock)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(_count);
            writer.Write(_namesLength);
            writer.Flush();
            WriteArray<int>(stream, _parent);
            WriteArray<int>(stream, _firstChild);
            WriteArray<int>(stream, _childCount);
            WriteArray<int>(stream, _nameStart);
            WriteArray<ushort>(stream, _nameLength);
            WriteArray<long>(stream, _size);
            WriteArray<long>(stream, _modified);
            WriteArray<byte>(stream, _flags);
            stream.Write(_names, 0, _namesLength);
        }

        void WriteArray<T>(Stream target, T[] array) where T : unmanaged =>
            target.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan(0, _count)));
    }

    /// <summary>Relit un arbre écrit par <see cref="Write"/> et vérifie sa cohérence (les données viennent d'un autre processus).</summary>
    public static FileTree Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Format d'arbre inconnu.");
        int count = reader.ReadInt32(), namesLength = reader.ReadInt32();
        if (count < 1 || namesLength < 0) throw new InvalidDataException("Arbre vide ou corrompu.");
        var tree = new FileTree(count, namesLength);
        ReadArray<int>(tree._parent);
        ReadArray<int>(tree._firstChild);
        ReadArray<int>(tree._childCount);
        ReadArray<int>(tree._nameStart);
        ReadArray<ushort>(tree._nameLength);
        ReadArray<long>(tree._size);
        ReadArray<long>(tree._modified);
        ReadArray<byte>(tree._flags);
        stream.ReadExactly(tree._names);

        for (int i = 0; i < count; i++)
        {
            bool parentValid = i == 0 ? tree._parent[i] == -1 : (uint)tree._parent[i] < (uint)i;
            bool childrenValid = tree._childCount[i] == 0 ||
                                 (tree._firstChild[i] > i && tree._childCount[i] > 0 && (long)tree._firstChild[i] + tree._childCount[i] <= count);
            bool nameValid = tree._nameStart[i] >= 0 && (long)tree._nameStart[i] + tree._nameLength[i] <= namesLength;
            if (!parentValid || !childrenValid || !nameValid) throw new InvalidDataException("Arbre incohérent.");
        }
        return tree;

        void ReadArray<T>(T[] array) where T : unmanaged =>
            stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan()));
    }

    FileTree(int count, int namesLength)
    {
        _parent = new int[count];
        _firstChild = new int[count];
        _childCount = new int[count];
        _nameStart = new int[count];
        _nameLength = new ushort[count];
        _size = new long[count];
        _modified = new long[count];
        _flags = new byte[count];
        _names = new byte[namesLength];
        _namesLength = namesLength;
        _count = count;
    }

    int Append(int parent, ChildBatch batch)
    {
        int first = _count, n = batch.Count;
        if (_count + n > _parent.Length)
        {
            int capacity = Math.Max(_count + n, _parent.Length * 2);
            Array.Resize(ref _parent, capacity);
            Array.Resize(ref _firstChild, capacity);
            Array.Resize(ref _childCount, capacity);
            Array.Resize(ref _nameStart, capacity);
            Array.Resize(ref _nameLength, capacity);
            Array.Resize(ref _size, capacity);
            Array.Resize(ref _modified, capacity);
            Array.Resize(ref _flags, capacity);
        }
        if (_namesLength + batch.NameBytes.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(_namesLength + batch.NameBytes.Length, _names.Length * 2));

        batch.NameBytes.CopyTo(_names.AsSpan(_namesLength));
        int offset = _namesLength;
        for (int i = 0; i < n; i++)
        {
            int id = first + i;
            _parent[id] = parent;
            _nameStart[id] = offset;
            _nameLength[id] = batch.NameLengths[i];
            _size[id] = batch.Sizes[i];
            _modified[id] = batch.Modified[i];
            _flags[id] = batch.Directories[i] ? DirectoryFlag : (byte)0;
            offset += batch.NameLengths[i];
        }
        _namesLength = offset;
        _count += n;
        return first;
    }
}

/// <summary>Tampon réutilisable contenant les entrées d'un dossier avant leur ajout à l'arbre.</summary>
public sealed class ChildBatch
{
    byte[] _names = new byte[4096];
    int _namesLength;
    readonly List<ushort> _nameLengths = [];
    readonly List<long> _sizes = [];
    readonly List<bool> _directories = [];
    readonly List<long> _modified = [];

    public int Count => _sizes.Count;
    internal ReadOnlySpan<byte> NameBytes => _names.AsSpan(0, _namesLength);
    internal List<ushort> NameLengths => _nameLengths;
    internal List<long> Sizes => _sizes;
    internal List<bool> Directories => _directories;
    internal List<long> Modified => _modified;

    /// <param name="modified">Date de modification au format FILETIME (UTC) ; 0 si inconnue.</param>
    public void Add(ReadOnlySpan<char> name, long size, bool isDirectory, long modified = 0)
    {
        // ponytail: un nom NTFS contenant un substitut UTF-16 isolé est stocké avec U+FFFD (chemin reconstruit inexact) ; passer à WTF-8 si le cas se présente.
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        if (_namesLength + max > _names.Length)
            Array.Resize(ref _names, Math.Max(_namesLength + max, _names.Length * 2));
        int written = Encoding.UTF8.GetBytes(name, _names.AsSpan(_namesLength));
        _namesLength += written;
        _nameLengths.Add(checked((ushort)written));
        _sizes.Add(isDirectory ? 0 : size);
        _directories.Add(isDirectory);
        _modified.Add(modified);
    }

    public void Clear()
    {
        _namesLength = 0;
        _nameLengths.Clear();
        _sizes.Clear();
        _directories.Clear();
        _modified.Clear();
    }
}
