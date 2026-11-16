using System.Runtime.InteropServices;
using Camembert.Core.Services;

namespace Camembert.Windows.Shell;

/// <summary>Élément à supprimer, tel qu'il était au moment de l'analyse.</summary>
/// <param name="ModifiedTicks">Date de modification relevée par l'analyse (FILETIME UTC) ; 0 si inconnue.</param>
public sealed record DeleteRequest(int Id, string Path, bool IsDirectory, long Size, long ModifiedTicks);

/// <param name="Problem">Raison pour laquelle l'élément n'a pas été supprimé ; null s'il est dans la Corbeille.</param>
public sealed record DeleteResult(DeleteRequest Request, bool Deleted, string? Problem);

/// <summary>
/// Suppression sécurisée : uniquement vers la Corbeille Windows (API Shell IFileOperation), jamais définitive
/// sans que Windows le demande explicitement, et seulement si l'élément est encore celui que l'analyse a vu.
/// </summary>
public static class SafeDelete
{
    /// <summary>
    /// Vérifie un élément juste avant sa suppression : emplacement permis, toujours présent, même nature,
    /// inchangé depuis l'analyse, sur un disque doté d'une Corbeille. Renvoie la raison du refus, ou null si tout est en ordre.
    /// </summary>
    public static string? Check(DeleteRequest request, DeletionRules rules)
    {
        if (rules.BlockReason(request.Path) is { } blocked) return "Emplacement protégé : " + blocked;
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(request.Path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return "Introuvable : déjà supprimé ou déplacé depuis l'analyse.";
            }
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (isDirectory != request.IsDirectory) return "A changé de nature depuis l'analyse (fichier ou dossier).";

            FileSystemInfo info = isDirectory ? new DirectoryInfo(request.Path) : new FileInfo(request.Path);
            if (info.LinkTarget is not null) return "Est devenu un lien depuis l'analyse.";
            bool modified = request.ModifiedTicks > 0 && info.LastWriteTimeUtc.ToFileTimeUtc() != request.ModifiedTicks;
            // Taille 0 dans l'analyse : fichier cloud non téléchargé ou second nom d'un lien physique, non comparable.
            if (!isDirectory && request.Size > 0 && ((FileInfo)info).Length != request.Size) modified = true;
            if (modified) return "Modifié depuis l'analyse : relancez l'analyse avant de le supprimer.";

            // Seuls les disques fixes ont une Corbeille : ailleurs (clé USB, réseau) Windows supprimerait pour de bon.
            string root = Path.GetPathRoot(request.Path)!;
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
                return $"Pas de Corbeille sur {root.TrimEnd('\\')} : la suppression serait définitive, elle est refusée.";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Accès refusé.";
        }
        catch (IOException e)
        {
            return "Illisible : " + ErrorText.Describe(e);
        }
    }

    /// <summary>
    /// Envoie les éléments à la Corbeille, un par un, chacun revérifié juste avant. Ne lève pas d'exception pour
    /// un élément en échec : chaque résultat dit ce qui s'est passé. À appeler après confirmation de l'utilisateur.
    /// </summary>
    /// <param name="owner">Fenêtre propriétaire d'un éventuel avertissement de Windows.</param>
    public static Task<List<DeleteResult>> SendToRecycleBinAsync(IReadOnlyList<DeleteRequest> requests, DeletionRules rules, IntPtr owner)
    {
        var completion = new TaskCompletionSource<List<DeleteResult>>();
        // L'API Shell exige un thread STA ; hors du thread d'interface pour ne pas la figer.
        var thread = new Thread(() =>
        {
            try
            {
                var results = new List<DeleteResult>(requests.Count);
                foreach (var request in requests)
                {
                    string? problem = Check(request, rules) ?? Recycle(request.Path, owner);
                    results.Add(new DeleteResult(request, problem is null, problem));
                }
                completion.SetResult(results);
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        }) { IsBackground = true, Name = "Camembert Corbeille" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    // ponytail: une opération Shell par élément (résultat individuel sans implémenter IFileOperationProgressSink) ;
    // regrouper en une seule opération avec un récepteur d'événements si des milliers d'éléments deviennent courants.
    static string? Recycle(string path, IntPtr owner)
    {
        IFileOperation? operation = null;
        IntPtr item = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out item);
            if (hr < 0) return path.Length >= 260 ? "Chemin trop long pour la Corbeille Windows." : Describe(hr);

            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!)!;
            operation.SetOperationFlags(Flags);
            operation.SetOwnerWindow(owner);
            operation.DeleteItem(item, IntPtr.Zero);
            hr = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (hr < 0 && unchecked((uint)hr) != 0x80270000) return Describe(hr);
            // Aucune boîte de Windows n'est affichée : une opération « abandonnée » l'a été par Windows, pas par l'utilisateur.
            if (aborted || hr < 0) return Blocked;
            return File.Exists(path) || Directory.Exists(path) ? "N'a pas pu être déplacé vers la Corbeille." : null;
        }
        catch (COMException e)
        {
            return Describe(e.HResult);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation is not null) Marshal.ReleaseComObject(operation);
        }
    }

    const string Blocked = "Windows n'a pas pu le déplacer vers la Corbeille : utilisé par un autre programme, ou accès refusé.";

    static string Describe(int hr) => unchecked((uint)hr) switch
    {
        0x80070005 => "Accès refusé.",
        0x80070020 or 0x80070021 => "Utilisé par un autre programme.",
        0x800704C7 => "Suppression annulée dans l'avertissement de Windows.",
        _ => $"Windows a refusé la suppression (code 0x{hr:X8}).",
    };

    // ALLOWUNDO + RECYCLEONDELETE : vers la Corbeille. NOCONFIRMATION, SILENT, NOERRORUI : pas de boîtes de Windows
    // (la confirmation et le compte rendu sont ceux de l'application). WANTNUKEWARNING : exception voulue, Windows
    // avertit et demande lui-même si un élément ne peut pas aller à la Corbeille (trop gros, Corbeille désactivée).
    const uint Flags = 0x0040 | 0x00080000 | 0x0010 | 0x0004 | 0x0400 | 0x4000;
    static readonly Guid FileOperationClass = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem;

    // Méthodes dans l'ordre exact de la table virtuelle ; seules quatre sont utilisées.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string copyName, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IntPtr item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid interfaceId, out IntPtr item);

}
