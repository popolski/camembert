using System.Windows;
using System.Windows.Input;
using Camembert.Core.Services;
using Camembert.Windows.Shell;
using Serilog;

namespace Camembert.App.ViewModels;

/// <summary>Une recommandation : mesure, explication, et l'action proposée s'il y en a une.</summary>
public sealed class CleanupCard : ViewModelBase
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required long Size { get; init; }
    public string SizeText => SizeFormat.Format(Size);
    /// <summary>Texte du bouton ; vide pour une simple information.</summary>
    public string ActionLabel { get; init; } = "";
    public ICommand? Command { get; init; }
    public bool HasAction => ActionLabel.Length > 0;
    public string ActionName => $"{ActionLabel} — {Name}"; // nom distinct pour les lecteurs d'écran

    string _status = "";
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
}

/// <summary>
/// Nettoyage recommandé, repris de la version Python : fichiers temporaires, caches, Corbeille, Téléchargements.
/// Les nettoyages envoient à la Corbeille (récupérable) ; seul « Vider la Corbeille » est définitif, et le dit.
/// </summary>
public sealed class CleanupViewModel : ViewModelBase
{
    const long Small = 1L << 20, Large = 100L << 20; // seuils d'affichage, comme la version Python

    readonly Action<string> _analyze;
    readonly DeletionRules _rules;
    CancellationTokenSource? _measuring;
    bool _busy;

    public CleanupViewModel(Action<string> analyze, DeletionRules rules)
    {
        _analyze = analyze;
        _rules = rules;
        RefreshCommand = new RelayCommand(_ => Refresh(), () => !_busy);
    }

    public ICommand RefreshCommand { get; }

    List<CleanupCard> _cards = [];
    public List<CleanupCard> Cards { get => _cards; private set => Set(ref _cards, value); }

    string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public void Cancel() => _measuring?.Cancel();

    /// <summary>Mesure tout en arrière-plan ; les cartes s'affichent quand c'est prêt.</summary>
    public async void Refresh()
    {
        _measuring?.Cancel();
        var measuring = _measuring = new CancellationTokenSource();
        Summary = "Recherche de ce qui peut être nettoyé…";
        try
        {
            var cards = await Task.Run(() => Measure(measuring.Token), measuring.Token);
            if (measuring.IsCancellationRequested) return;
            Cards = cards;
            Summary = cards.Count == 0
                ? "Rien à recommander pour le moment."
                : $"Au total, {SizeFormat.Format(cards.Sum(c => c.Size))} identifiés. Les nettoyages envoient à la Corbeille : rien n'est perdu tant qu'elle n'est pas vidée.";
        }
        catch (OperationCanceledException)
        {
            // Remplacée par une mesure plus récente, ou écran quitté.
        }
    }

    List<CleanupCard> Measure(CancellationToken cancellation)
    {
        var cards = new List<CleanupCard>();
        var now = DateTime.UtcNow;
        foreach (var category in CleanupCatalog.Categories)
        {
            var items = CleanupCatalog.FindItems(category, now, cancellation);
            long size = items.Sum(i => i.Size);
            if (size < Small) continue;
            // La vue passe la carte elle-même en paramètre de commande, pour y afficher l'avancement.
            cards.Add(category.ByWindows is null
                ? new CleanupCard
                {
                    Name = category.Name, Description = category.Description, Size = size, ActionLabel = "Mettre à la Corbeille…",
                    Command = new RelayCommand(card => _ = CleanAsync(category, (CleanupCard)card!), () => !_busy),
                }
                : new CleanupCard
                {
                    Name = category.Name, Description = category.Description + " Camembert n'y supprime rien : " + category.ByWindows + ".",
                    Size = size, ActionLabel = "Ouvrir Stockage", Command = new RelayCommand(_ => OpenStorage()),
                });
        }

        var (binSize, binCount) = RecycleBin.Measure();
        if (binCount > 0)
            cards.Add(new CleanupCard
            {
                Name = "Corbeille", Size = binSize, ActionLabel = "Vider la Corbeille…",
                Description = $"{binCount} élément(s), sur tous les disques. C'est là que vont les éléments mis à la Corbeille par Camembert. La vider est définitif : c'est la seule action irréversible de l'application.",
                Command = new RelayCommand(self => _ = EmptyBinAsync((CleanupCard)self!), () => !_busy),
            });

        string downloads = KnownFolders.Downloads();
        long downloadsSize = Directory.Exists(downloads) ? CleanupCatalog.DirectorySize(new DirectoryInfo(downloads), cancellation) : 0;
        if (downloadsSize >= Large)
            cards.Add(new CleanupCard
            {
                Name = "Téléchargements", Size = downloadsSize, ActionLabel = "Analyser",
                Description = "À trier vous-même : installeurs et archives déjà utilisés s'y accumulent. « Analyser » l'ouvre dans le diagramme.",
                Command = new RelayCommand(_ => _analyze(downloads)),
            });

        string system = Path.GetPathRoot(Environment.SystemDirectory)!;
        long hiberfil = new DirectoryInfo(system).EnumerateFiles("hiberfil.sys", new EnumerationOptions { AttributesToSkip = 0 }).Sum(f => f.Length);
        if (hiberfil >= Large)
            cards.Add(new CleanupCard
            {
                Name = "hiberfil.sys", Size = hiberfil,
                Description = "Fichier de la mise en veille prolongée. Il disparaît si vous désactivez celle-ci avec la commande « powercfg /h off » (en administrateur) ; Camembert n'y touche pas.",
            });
        var windowsOld = new DirectoryInfo(Path.Combine(system, "Windows.old"));
        long windowsOldSize = windowsOld.Exists ? CleanupCatalog.DirectorySize(windowsOld, cancellation) : 0;
        if (windowsOldSize >= Large)
            cards.Add(new CleanupCard
            {
                Name = "Windows.old", Size = windowsOldSize, ActionLabel = "Ouvrir Stockage", Command = new RelayCommand(_ => OpenStorage()),
                Description = "Ancienne installation de Windows (taille minimale : une partie n'est pas lisible). À supprimer par Paramètres Windows › Système › Stockage › Recommandations de nettoyage ; Camembert n'y touche pas.",
            });

        return cards.OrderByDescending(c => c.Size).ToList();
    }

    async Task CleanAsync(CleanupCategory category, CleanupCard card)
    {
        if (_busy) return;
        var window = Application.Current.MainWindow;
        SetBusy(true);
        try
        {
            card.Status = "Préparation…";
            // Liste refaite maintenant : ce qui sera confirmé est exactement ce qui sera envoyé.
            var items = await Task.Run(() => CleanupCatalog.FindItems(category, DateTime.UtcNow));
            long total = items.Sum(i => i.Size);
            card.Status = "";
            if (items.Count == 0) { card.Status = "Plus rien à nettoyer ici."; return; }

            string question = $"Mettre à la Corbeille {items.Count} élément(s), {SizeFormat.Format(total)}, de « {category.Name} » ?\n\n" +
                              category.Description +
                              "\n\nIls resteront récupérables dans la Corbeille ; l'espace sera libéré quand vous la viderez.";
            if (MessageBox.Show(window, question, "Camembert — nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var requests = items.Select((item, i) => new DeleteRequest(i, item.Path, item.IsDirectory, item.IsDirectory ? 0 : item.Size, item.ModifiedTicks)).ToList();
            var progress = new Progress<int>(done => card.Status = $"Envoi à la Corbeille : {done} / {requests.Count}…");
            var results = await SafeDelete.SendToRecycleBinAsync(requests, _rules, new System.Windows.Interop.WindowInteropHelper(window).Handle, progress);

            int deleted = results.Count(r => r.Deleted), left = results.Count - deleted;
            long moved = results.Where(r => r.Deleted).Sum(r => items[r.Request.Id].Size);
            Log.Information("Nettoyage « {Category} » : {Deleted} éléments mis à la Corbeille ({Bytes} octets), {Left} laissés en place",
                category.Name, deleted, moved, left);
            foreach (var failure in results.Where(r => !r.Deleted).Take(50))
                Log.Information("  laissé : {Path} — {Problem}", failure.Request.Path, failure.Problem);
            card.Status = $"{deleted} élément(s) mis à la Corbeille ({SizeFormat.Format(moved)})." +
                          (left > 0 ? $" {left} laissé(s) en place : utilisés par un programme ouvert, protégés ou modifiés entre-temps." : "");
            await Task.Delay(2500); // laisser lire le compte rendu avant de remesurer
        }
        finally
        {
            SetBusy(false);
        }
        Refresh();
    }

    async Task EmptyBinAsync(CleanupCard card)
    {
        if (_busy) return;
        var window = Application.Current.MainWindow;
        var (size, count) = RecycleBin.Measure();
        string question = $"Vider la Corbeille ?\n\n{count} élément(s), {SizeFormat.Format(size)}, sur tous les disques.\n\n" +
                          "C'est définitif : ces éléments ne pourront plus être restaurés.";
        if (MessageBox.Show(window, question, "Camembert — vider la Corbeille", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SetBusy(true);
        try
        {
            card.Status = "Vidage de la Corbeille…";
            string? error = await RecycleBin.EmptyAsync(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            var (after, remaining) = RecycleBin.Measure();
            Log.Information("Corbeille vidée : {Before} octets avant, {After} après ({Error})", size, after, error ?? "succès");
            card.Status = error ?? $"Corbeille vidée : {SizeFormat.Format(Math.Max(size - after, 0))} libérés." +
                                   (remaining > 0 ? $" {remaining} élément(s) restants (utilisés ou protégés)." : "");
            await Task.Delay(2500);
        }
        finally
        {
            SetBusy(false);
        }
        Refresh();
    }

    static void OpenStorage()
    {
        try { KnownFolders.OpenStorageSettings(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("Impossible d'ouvrir les paramètres de stockage : " + ErrorText.Describe(e) + ".", "Camembert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        CommandManager.InvalidateRequerySuggested();
    }
}
