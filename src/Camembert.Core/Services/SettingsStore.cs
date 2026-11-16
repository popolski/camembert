using System.Text.Json;
using System.Text.Json.Serialization;

namespace Camembert.Core.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Réglages de l'utilisateur. Les valeurs par défaut sont celles d'un premier lancement.</summary>
public sealed record AppSettings
{
    /// <summary>Par défaut, le thème suit celui de Windows.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;
    /// <summary>Animations du diagramme (elles restent coupées si Windows a ses animations désactivées).</summary>
    public bool Animations { get; init; } = true;
}

/// <summary>
/// Réglages en JSON dans un dossier donné : en mode portable, celui de l'exécutable.
/// Rien n'est écrit ailleurs ; si le dossier n'est pas inscriptible, <see cref="Save"/> le dit.
/// </summary>
public sealed class SettingsStore(string directory)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = Path.Combine(directory, "Camembert.settings.json");

    /// <summary>Réglages enregistrés ; ceux par défaut si le fichier est absent, illisible ou invalide.</summary>
    public AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Enregistre les réglages. Renvoie null en cas de succès, sinon la raison de l'échec.</summary>
    public string? Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ErrorText.Describe(e);
        }
    }
}
