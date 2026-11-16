namespace Camembert.Windows.Shell;

public sealed record VolumeInfo(string Root, string Label, string FileSystem, long Capacity, long Free)
{
    public long Used => Capacity - Free;
}

public static class VolumeService
{
    /// <summary>Volumes locaux prêts (disques fixes et amovibles). Peut attendre le réveil d'un disque : à appeler hors du thread d'interface.</summary>
    public static List<VolumeInfo> GetLocalVolumes() =>
        DriveInfo.GetDrives().Select(Read).OfType<VolumeInfo>().ToList();

    /// <summary>Volume contenant <paramref name="path"/> (seul ce volume est interrogé), ou null s'il n'est pas local.</summary>
    public static VolumeInfo? Find(string path)
    {
        try
        {
            return Read(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!));
        }
        catch (ArgumentException)
        {
            return null; // chemin réseau
        }
    }

    static VolumeInfo? Read(DriveInfo drive)
    {
        try
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) return null;
            return new(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // volume retiré ou verrouillé pendant la lecture
        }
    }
}
