using Camembert.Core.Models;

namespace Camembert.Core.Interfaces;

/// <summary>Contrat commun aux moteurs d'analyse (standard aujourd'hui, NTFS/MFT au lot 2).</summary>
public interface IScanEngine
{
    /// <summary>Nom affiché à l'utilisateur, pour ne jamais présenter un moteur pour un autre.</summary>
    string Name { get; }

    /// <summary>
    /// Analyse <paramref name="rootPath"/> et renvoie l'arbre agrégé. Bloquant : à appeler hors du thread d'interface.
    /// Lève <see cref="OperationCanceledException"/> en cas d'annulation.
    /// </summary>
    FileTree Scan(string rootPath, ScanProgress progress, CancellationToken cancellation);
}

/// <summary>Compteurs mis à jour par le moteur pendant l'analyse et lus périodiquement par l'interface.</summary>
public sealed class ScanProgress
{
    public long Files;
    public long Directories;
    public long Bytes;
    public long Errors;
    public volatile string CurrentPath = "";
}
