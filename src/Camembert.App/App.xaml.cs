using System.Windows;
using Camembert.App.ViewModels;
using Camembert.App.Views;
using Camembert.Scanner.Ntfs;
using Camembert.Windows.Elevation;
using Serilog;
using Camembert.Core.Services;

namespace Camembert.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Processus d'analyse élevé, lancé par l'interface : aucune fenêtre, résultat renvoyé par le canal nommé.
        if (e.Args is [ElevatedScan.Switch, var root, var pipeName])
        {
            StartLogging("analyse élevée");
            int code;
            try
            {
                Log.Information("Analyse rapide de {Root}", root);
                code = ElevatedScan.RunHost(new NtfsScanEngine(), root, pipeName);
                Log.Information("Fin de l'analyse rapide, code {Code}", code);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Canal injoignable"); // l'interface constate l'arrêt du processus
                code = 1;
            }
            Log.CloseAndFlush();
            Shutdown(code);
            return;
        }

        StartLogging("interface");
        Log.Information("Démarrage de Camembert {Version} sur {Os}, administrateur : {Admin}",
            typeof(App).Assembly.GetName().Version, Environment.OSVersion.VersionString, Environment.IsPrivilegedProcess);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Erreur fatale");
            Log.CloseAndFlush();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Erreur inattendue");
            MessageBox.Show("Une erreur inattendue s'est produite : " + ErrorText.Describe(args.Exception) + ".\n\nLe détail est dans le journal (dossier « journaux »).", "Camembert",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var viewModel = new MainViewModel(); // applique aussi le thème enregistré
        new MainWindow { DataContext = viewModel }.Show();

        // Camembert.exe <dossier> : lance directement l'analyse de ce dossier (analyse rapide si c'est un volume NTFS).
        // « C: » seul désignerait le dossier courant de ce disque : on comprend la racine « C:\ ».
        string? start = e.Args.Length > 0 ? (e.Args[0].Length == 2 && e.Args[0][1] == ':' ? e.Args[0] + "\\" : e.Args[0]) : null;
        if (start is not null && Directory.Exists(start))
            viewModel.ScanCommand.Execute(start);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Journal local, jamais envoyé : dossier « journaux » à côté de l'exécutable s'il est inscriptible, sinon
    /// %LOCALAPPDATA%\Camembert\journaux. Un fichier par jour, dix jours conservés.
    /// </summary>
    static void StartLogging(string role)
    {
        string? directory = WritableDirectory(Path.Combine(AppContext.BaseDirectory, "journaux"))
                            ?? WritableDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Camembert", "journaux"));
        if (directory is null) return; // aucun emplacement inscriptible : pas de journal, l'application fonctionne quand même
        Log.Logger = new LoggerConfiguration()
            .Enrich.WithProperty("Role", role)
            .WriteTo.File(Path.Combine(directory, "camembert-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                shared: true, // l'interface et le processus d'analyse élevé écrivent dans le même fichier
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({Role}) {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    static string? WritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".ecriture-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return directory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
