using System.ComponentModel;
using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.Core.Tests;

public class ErrorTextTests
{
    static Exception Caught(Action action)
    {
        try { action(); }
        catch (Exception e) { return e; }
        throw new InvalidOperationException("aucune exception");
    }

    [Fact]
    public void Dotnet_messages_are_replaced_by_french_text()
    {
        string missing = Path.Combine(Path.GetTempPath(), "camembert-n-existe-pas-" + Guid.NewGuid().ToString("N"), "x.txt");
        Assert.Equal("introuvable", ErrorText.Describe(Caught(() => File.ReadAllText(missing))));
        Assert.Equal("accès refusé", ErrorText.Describe(new UnauthorizedAccessException("Access to the path 'C:\\Données' is denied.")));
        Assert.Equal("utilisé par un autre programme", ErrorText.Describe(new IOException("The process cannot access the file", unchecked((int)0x80070020))));
        Assert.Equal("format non reconnu ou données endommagées", ErrorText.Describe(Caught(() => int.Parse("abc"))));
    }

    [Fact]
    public void Camembert_and_windows_messages_are_kept()
    {
        // Levée par le code de Camembert : message français conservé, même pour un type .NET courant.
        var own = Caught(() => FileTree.Read(new MemoryStream(new byte[16])));
        Assert.Equal("Format d'arbre inconnu", ErrorText.Describe(own));
        // Texte fourni par Windows, dans la langue du système.
        var win32 = new Win32Exception(5);
        Assert.Equal(win32.Message.TrimEnd('.'), ErrorText.Describe(win32));
    }
}
