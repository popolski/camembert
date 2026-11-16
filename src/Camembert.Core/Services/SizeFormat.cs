using System.Globalization;

namespace Camembert.Core.Services;

public static class SizeFormat
{
    static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    static readonly string[] Units = ["Ko", "Mo", "Go", "To", "Po"];

    /// <summary>Taille lisible en unités binaires, comme l'Explorateur Windows (1 Ko = 1024 octets).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024) return bytes + (bytes > 1 ? " octets" : " octet");
        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        string format = value < 10 ? "0.00" : value < 100 ? "0.0" : "0";
        return value.ToString(format, French) + " " + Units[unit];
    }

    /// <summary>Lit « 500 Mo », « 1,5 Go », « 200 ko »… Un nombre seul est en Mo. Renvoie false si le texte n'est pas une taille.</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim().Replace('.', ',');
        int split = 0;
        while (split < text.Length && (char.IsDigit(text[split]) || text[split] == ',')) split++;
        if (!double.TryParse(text[..split], NumberStyles.AllowDecimalPoint, French, out double value)) return false;
        long factor = text[split..].Trim().ToLowerInvariant() switch
        {
            "o" or "octet" or "octets" => 1,
            "k" or "ko" or "kb" => 1L << 10,
            "" or "m" or "mo" or "mb" => 1L << 20,
            "g" or "go" or "gb" => 1L << 30,
            "t" or "to" or "tb" => 1L << 40,
            _ => 0,
        };
        if (factor == 0) return false;
        bytes = (long)(value * factor);
        return true;
    }

    public static string Count(long n) => n.ToString("N0", French);
}
