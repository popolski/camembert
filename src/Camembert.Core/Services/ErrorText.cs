using System.Runtime.InteropServices;

namespace Camembert.Core.Services;

/// <summary>
/// Texte français d'une erreur, pour l'interface. Les messages écrits par Camembert sont en français ; ceux des
/// exceptions .NET sont en anglais (la version portable n'embarque pas de traductions) et sont donc remplacés ;
/// ceux de Windows (Win32, COM) sont dans la langue du système et sont gardés.
/// </summary>
public static class ErrorText
{
    public static string Describe(Exception e)
    {
        if (e.TargetSite?.DeclaringType?.Assembly.GetName().Name?.StartsWith("Camembert", StringComparison.Ordinal) == true)
            return e.Message.TrimEnd('.'); // levée par Camembert : message déjà en français
        return e switch
        {
            System.ComponentModel.Win32Exception or ExternalException => e.Message.TrimEnd('.'), // texte de Windows
            UnauthorizedAccessException => "accès refusé",
            FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => "introuvable",
            PathTooLongException => "chemin trop long",
            EndOfStreamException => "données incomplètes",
            IOException => unchecked((uint)e.HResult) switch
            {
                0x80070020 or 0x80070021 => "utilisé par un autre programme",
                0x80070070 => "disque plein",
                0x80070015 => "périphérique non prêt",
                0x80070017 => "erreur de lecture du disque",
                _ => $"erreur d'entrée-sortie (code 0x{e.HResult:X8})",
            },
            NotSupportedException or ArgumentException or InvalidOperationException or FormatException => "format non reconnu ou données endommagées",
            _ => $"erreur inattendue ({e.GetType().Name})",
        };
    }
}
