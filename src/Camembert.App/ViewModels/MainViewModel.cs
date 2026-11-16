using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Camembert.App.Controls;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;
using Camembert.Core.Services;
using Camembert.Scanner.Ntfs;
using Camembert.Scanner.Standard;
using Camembert.Windows.Elevation;
using Camembert.Windows.Shell;
using Camembert.App.Themes;
using Microsoft.Win32;
using Serilog;

namespace Camembert.App.ViewModels;

public sealed record VolumeRow(VolumeInfo Volume)
{
    public string Root => Volume.Root;
    public string Title => (Volume.Label.Length > 0 ? Volume.Label : "Disque local") + " (" + Volume.Root.TrimEnd('\\') + ")";
    public double UsedPercent => Volume.Capacity > 0 ? 100.0 * Volume.Used / Volume.Capacity : 0;
    public string Detail =>
        $"{SizeFormat.Format(Volume.Used)} utilisés sur {SizeFormat.Format(Volume.Capacity)} — {SizeFormat.Format(Volume.Free)} libres — {Volume.FileSystem}";

    /// <summary>L'analyse rapide (lecture de la MFT) n'existe que pour NTFS.</summary>
    public bool FastAvailable => Volume.FileSystem == "NTFS";
    public string ScanNote => !FastAvailable ? "Analyse standard"
        : Environment.IsPrivilegedProcess ? "Analyse rapide NTFS"
        : "Analyse rapide NTFS — demande les droits d'administrateur";
}

public sealed record Crumb(int Id, string Name)
{
    public override string ToString() => Name; // nom lu par les lecteurs d'écran
}

public sealed class MainViewModel : ViewModelBase
{
    enum Screen { Home, Scanning, Result }

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Stopwatch _clock = new();
    CancellationTokenSource? _cancellation, _searchCancellation;
    ScanProgress _progress = new();
    VolumeInfo? _volume;       // volume contenant la racine analysée
    bool _wholeVolume;         // la racine analysée est la racine du volume
    bool _fastRequested;       // dernier choix de moteur, repris par « Actualiser »
    string _rootPath = "";
    TreeSearch? _search;
    Basket? _basket;
    readonly DeletionRules _rules = DeletionRules.FromEnvironment();
    // Mode portable : réglages à côté de l'exécutable.
    readonly SettingsStore _settingsStore = new(AppContext.BaseDirectory);
    AppSettings _settings;

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        ThemeService.Apply(_settings.Theme);
        _settingsLocation = "Réglages enregistrés dans " + _settingsStore.FilePath;
        ScanCommand = new RelayCommand(path => _ = ScanAsync((string)path!, fast: true));
        StandardScanCommand = new RelayCommand(path => _ = ScanAsync((string)path!, fast: false));
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder());
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel());
        HomeCommand = new RelayCommand(_ => ShowHome(""));
        RefreshCommand = new RelayCommand(_ => _ = ScanAsync(_rootPath, _fastRequested));
        EnterCommand = new RelayCommand(target => { if (Single(target) is int id && Tree!.IsDirectory(id)) Navigate(id); });
        EnterSelectedCommand = new RelayCommand(_ => Navigate(_selectedNode),
            () => Tree is not null && _selectedNode != SunburstControl.NoNode && Tree.IsDirectory(_selectedNode));
        UpCommand = new RelayCommand(_ => Navigate(Tree!.Parent(Current)), () => CanGoUp);
        LocateCommand = new RelayCommand(target => { if (Single(target) is int id) Locate(id); });
        RevealCommand = new RelayCommand(target => { if (Single(target) is int id) Reveal(id); });
        CopyPathCommand = new RelayCommand(CopyPaths);
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());
        PreviewCommand = new RelayCommand(ShowPreview, () => HasFocus);
        AddToBasketCommand = new RelayCommand(AddToBasket, () => HasFocus && !IsDeleting);
        RemoveFromBasketCommand = new RelayCommand(RemoveFromBasket, () => !IsDeleting);
        ClearBasketCommand = new RelayCommand(_ => { _basket?.Clear(); RefreshBasket(""); }, () => HasBasketItems && !IsDeleting);
        DeleteBasketCommand = new RelayCommand(_ => _ = DeleteBasketAsync(), () => HasBasketItems && !IsDeleting);
        RevealFocusCommand = new RelayCommand(_ => Reveal(_focusNode), () => HasFocus);

        ContentList.UserSelected = row => { if (row is not null) SelectedNode = row.Id; };
        SearchList.UserSelected = row => { if (row is not null && Tree is not null) { SetFocus(row.Id); ShowInfo(row.Id); } };
        BasketList.UserSelected = row => { if (row is not null && Tree is not null) { SetFocus(row.Id); ShowInfo(row.Id); } };
        _timer.Tick += (_, _) => UpdateProgress();
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); RunSearch(); };
        ShowHome("");
    }

    public ICommand ScanCommand { get; }
    public ICommand StandardScanCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand RefreshCommand { get; }
    /// <summary>Entrer dans un dossier. Paramètre : identifiant, ou rien pour l'élément sélectionné.</summary>
    public ICommand EnterCommand { get; }
    public ICommand EnterSelectedCommand { get; }
    public ICommand UpCommand { get; }
    /// <summary>Afficher un élément dans son dossier, côté diagramme et liste.</summary>
    public ICommand LocateCommand { get; }
    public ICommand RevealCommand { get; }
    /// <summary>Copier le chemin. Paramètre : tableau d'identifiants, ou rien pour l'élément sélectionné.</summary>
    public ICommand CopyPathCommand { get; }
    public ICommand ClearSearchCommand { get; }
    /// <summary>Aperçu. Paramètre : identifiant, ou rien pour l'élément choisi.</summary>
    public ICommand PreviewCommand { get; }
    /// <summary>Mettre au panier (ne supprime rien). Paramètre : tableau d'identifiants, ou rien pour l'élément choisi.</summary>
    public ICommand AddToBasketCommand { get; }
    public ICommand RemoveFromBasketCommand { get; }
    public ICommand ClearBasketCommand { get; }
    /// <summary>Envoyer le contenu du panier à la Corbeille Windows, après confirmation.</summary>
    public ICommand DeleteBasketCommand { get; }
    public ICommand RevealFocusCommand { get; }

    // ---------------------------------------------------------------- écrans

    Screen _screen;
    public bool IsHome => _screen == Screen.Home;
    public bool IsScanning => _screen == Screen.Scanning;
    public bool IsResult => _screen == Screen.Result;

    void Show(Screen screen)
    {
        _screen = screen;
        Raise(nameof(IsHome));
        Raise(nameof(IsScanning));
        Raise(nameof(IsResult));
    }

    async void ShowHome(string message)
    {
        HomeMessage = message;
        Show(Screen.Home);
        // Hors du thread d'interface : un disque dur en veille met plusieurs secondes à répondre.
        Volumes = await Task.Run(() => VolumeService.GetLocalVolumes().Select(v => new VolumeRow(v)).ToList());
    }

    List<VolumeRow> _volumes = [];
    public List<VolumeRow> Volumes { get => _volumes; private set => Set(ref _volumes, value); }

    string _homeMessage = "";
    public string HomeMessage { get => _homeMessage; private set => Set(ref _homeMessage, value); }

    void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choisir un dossier à analyser" };
        if (dialog.ShowDialog() == true) _ = ScanAsync(dialog.FolderName, fast: false);
    }

    // ---------------------------------------------------------------- analyse

    string _scanTitle = "", _scanCounters = "", _scanPath = "";
    double _scanPercent;
    bool _scanIndeterminate;
    public string ScanTitle { get => _scanTitle; private set => Set(ref _scanTitle, value); }
    public string ScanCounters { get => _scanCounters; private set => Set(ref _scanCounters, value); }
    public string ScanPath { get => _scanPath; private set => Set(ref _scanPath, value); }
    public double ScanPercent { get => _scanPercent; private set => Set(ref _scanPercent, value); }
    public bool ScanIndeterminate { get => _scanIndeterminate; private set => Set(ref _scanIndeterminate, value); }

    /// <summary>
    /// Moteurs à essayer dans l'ordre. L'analyse rapide ne concerne qu'un volume NTFS entier ; sans privilèges,
    /// elle passe par un second processus élevé pour que l'interface reste non privilégiée.
    /// </summary>
    List<IScanEngine> EnginesFor(bool fast)
    {
        var engines = new List<IScanEngine>();
        if (fast && _wholeVolume && NtfsScanEngine.Supports(_rootPath))
        {
            var ntfs = new NtfsScanEngine();
            engines.Add(Environment.IsPrivilegedProcess ? ntfs : new ElevatedScan.Client(Environment.ProcessPath!, ntfs.Name));
        }
        engines.Add(new StandardScanEngine());
        return engines;
    }

    async Task ScanAsync(string path, bool fast)
    {
        if (IsScanning) return;
        // Libère l'arbre précédent avant d'en construire un nouveau.
        Tree = null;
        Segments = null;
        _search = null;
        _basket = null;
        _searchCancellation?.Cancel();
        ContentList.Load(null, [], null);
        SearchList.Load(null, [], null);
        RefreshBasket("");
        SetFocus(SunburstControl.NoNode);

        _rootPath = Path.GetFullPath(path);
        _fastRequested = fast;
        _cancellation = new CancellationTokenSource();
        ScanTitle = $"Analyse de {_rootPath} en cours…";
        ScanCounters = ScanPath = "";
        ScanIndeterminate = true;
        Show(Screen.Scanning);
        _volume = await Task.Run(() => VolumeService.Find(_rootPath));
        _wholeVolume = _volume is not null && string.Equals(_volume.Root, _rootPath, StringComparison.OrdinalIgnoreCase);
        _timer.Start();

        var token = _cancellation.Token;
        string fallbackNote = "";
        try
        {
            foreach (var engine in EnginesFor(fast))
            {
                _progress = new ScanProgress();
                ScanIndeterminate = !_wholeVolume;
                ScanPercent = 0;
                _clock.Restart();
                UpdateProgress();
                var progress = _progress;
                FileTree tree;
                try
                {
                    tree = await Task.Factory.StartNew(() => engine.Scan(_rootPath, progress, token),
                        token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception e) when (engine is not StandardScanEngine && e is UnauthorizedAccessException or NotSupportedException
                                              or InvalidOperationException or InvalidDataException or IOException or Win32Exception)
                {
                    Log.Warning(e, "{Engine} indisponible pour {Root}, repli sur l'analyse standard", engine.Name, _rootPath);
                    // Transparence : l'analyse standard n'est jamais présentée comme une analyse rapide.
                    fallbackNote = $" — analyse rapide indisponible ({ErrorText.Describe(e)})";
                    continue;
                }
                _clock.Stop();
                Log.Information("{Engine} de {Root} : {Files} fichiers, {Directories} dossiers, {Bytes} octets, {Errors} dossiers inaccessibles, {Seconds:0.0} s",
                    engine.Name, _rootPath, progress.Files, progress.Directories, tree.Size(FileTree.Root), progress.Errors, _clock.Elapsed.TotalSeconds);
                // La construction a laissé des centaines de Mo de tableaux intermédiaires : les rendre tout de suite.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                Tree = tree;
                _search = new TreeSearch(tree);
                _basket = new Basket(tree);
                RefreshBasket("");
                StatusText = BuildStatus(tree, progress, engine.Name) + fallbackNote;
                Navigate(FileTree.Root);
                Show(Screen.Result);
                ScheduleSearch();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Analyse de {Root} annulée", _rootPath);
            ShowHome("Analyse annulée.");
        }
        catch (Exception e)
        {
            Log.Error(e, "Échec de l'analyse de {Root}", _rootPath);
            ShowHome($"L'analyse de {_rootPath} a échoué : {ErrorText.Describe(e)}.");
        }
        finally
        {
            _timer.Stop();
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    void UpdateProgress()
    {
        long files = Interlocked.Read(ref _progress.Files), bytes = Interlocked.Read(ref _progress.Bytes);
        ScanCounters = $"{SizeFormat.Count(files)} fichiers — {SizeFormat.Format(bytes)} — {_clock.Elapsed.TotalSeconds:0} s";
        ScanPath = _progress.CurrentPath;
        if (_wholeVolume && _volume!.Used > 0) ScanPercent = Math.Min(100.0 * bytes / _volume.Used, 100);
    }

    string BuildStatus(FileTree tree, ScanProgress progress, string engineName)
    {
        long analysed = tree.Size(FileTree.Root);
        string status = $"{SizeFormat.Count(progress.Files)} fichiers, {SizeFormat.Count(progress.Directories)} dossiers — " +
                        $"{SizeFormat.Format(analysed)} analysés en {_clock.Elapsed.TotalSeconds:0.0} s ({engineName})";
        if (progress.Errors > 0)
            status += $" — {SizeFormat.Count(progress.Errors)} dossiers inaccessibles";
        if (_volume is not null)
        {
            status += $" — {_volume.Root.TrimEnd('\\')} : {SizeFormat.Format(_volume.Used)} utilisés, {SizeFormat.Format(_volume.Free)} libres";
            if (_wholeVolume)
            {
                long gap = _volume.Used - analysed;
                status += $" — écart avec Windows : {(gap < 0 ? "−" : "")}{SizeFormat.Format(Math.Abs(gap))}";
            }
        }
        return status;
    }

    // ---------------------------------------------------------------- résultat et navigation

    FileTree? _tree;
    public FileTree? Tree { get => _tree; private set => Set(ref _tree, value); }

    IReadOnlyList<SunburstSegment>? _segments;
    public IReadOnlyList<SunburstSegment>? Segments { get => _segments; private set => Set(ref _segments, value); }

    /// <summary>Contenu du dossier courant.</summary>
    public EntryListViewModel ContentList { get; } = new(ListKind.Content);
    public EntryListViewModel SearchList { get; } = new(ListKind.Search);
    public EntryListViewModel BasketList { get; } = new(ListKind.Basket);
    public PreviewViewModel Preview { get; } = new();

    List<Crumb> _crumbs = [];
    public List<Crumb> Crumbs { get => _crumbs; private set => Set(ref _crumbs, value); }

    string _statusText = "", _currentName = "", _currentSizeText = "", _listHeader = "", _infoTitle = "", _infoPath = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string CurrentName { get => _currentName; private set { if (Set(ref _currentName, value)) Raise(nameof(CenterName)); } }
    /// <summary>Nom court pour le centre du diagramme : le dernier segment quand la racine analysée est un chemin long.</summary>
    public string CenterName => Path.GetFileName(_currentName.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : _currentName;
    public string CurrentSizeText { get => _currentSizeText; private set => Set(ref _currentSizeText, value); }
    public string ListHeader { get => _listHeader; private set => Set(ref _listHeader, value); }
    public string InfoTitle { get => _infoTitle; private set => Set(ref _infoTitle, value); }
    public string InfoPath { get => _infoPath; private set => Set(ref _infoPath, value); }

    int _selectedTab;
    /// <summary>0 = contenu du dossier, 1 = recherche, 2 = aperçu.</summary>
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    /// <summary>Dossier affiché au centre du diagramme et dans la liste.</summary>
    public int Current { get; private set; }
    public bool CanGoUp => IsResult && Tree is not null && Current != FileTree.Root;

    int _selectedNode = SunburstControl.NoNode, _hoveredNode = SunburstControl.NoNode;
    Func<int, EntryRow>? _createContentRow;

    public int SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            if (value != SunburstControl.NoNode) SetFocus(value);
            SyncListSelection();
            ShowInfo(_hoveredNode != SunburstControl.NoNode ? _hoveredNode : value);
        }
    }

    public int HoveredNode
    {
        get => _hoveredNode;
        set { if (Set(ref _hoveredNode, value)) ShowInfo(value != SunburstControl.NoNode ? value : _selectedNode); }
    }

    /// <summary>Sélectionne dans la liste l'élément choisi dans le diagramme, ou son ancêtre affiché dans la liste.</summary>
    void SyncListSelection()
    {
        if (Tree is null || _createContentRow is null) return;
        int node = _selectedNode;
        while (node > FileTree.Root && Tree.Parent(node) != Current) node = Tree.Parent(node);
        var row = node > FileTree.Root ? _createContentRow(node) : null;
        if (!Equals(row, ContentList.SelectedRow)) ContentList.Select(row);
    }

    void Navigate(int node)
    {
        var tree = Tree!;
        Current = node;
        long total = tree.Size(node);

        var segments = SunburstLayout.Build(tree, node);
        var hues = new Dictionary<int, float>();
        foreach (var s in segments)
            if (s.Depth == 1 && s.Node != SunburstControl.NoNode) hues[s.Node] = s.Hue;

        var children = tree.ChildrenBySize(node);
        TreeSearch.Sort(tree, children, ContentList.Key, ContentList.Descending);
        _createContentRow = id => new EntryRow(tree, id, total,
            hues.TryGetValue(id, out float hue) ? Palette.Brush(hue, tree.IsDirectory(id)) : Palette.SmallItemBrush, showLocation: false);

        var crumbs = new List<Crumb>();
        for (int i = node; i >= FileTree.Root; i = tree.Parent(i)) crumbs.Insert(0, new Crumb(i, tree.Name(i)));

        _selectedNode = _hoveredNode = SunburstControl.NoNode;
        SetFocus(SunburstControl.NoNode);
        Raise(nameof(SelectedNode));
        Raise(nameof(HoveredNode));
        Segments = segments;
        ContentList.Load(tree, children, _createContentRow);
        Crumbs = crumbs;
        CurrentName = node == FileTree.Root ? _rootPath : tree.Name(node);
        CurrentSizeText = SizeFormat.Format(total);
        ListHeader = $"{SizeFormat.Count(children.Length)} éléments dans {CurrentName}";
        ShowInfo(SunburstControl.NoNode);
        Raise(nameof(CanGoUp));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Ouvre le dossier parent de l'élément et le sélectionne dans le diagramme et la liste.</summary>
    void Locate(int node)
    {
        if (Tree is null || node <= FileTree.Root) return;
        Navigate(Tree.Parent(node));
        SelectedTab = 0;
        SelectedNode = node;
    }

    void ShowInfo(int node)
    {
        if (Tree is null || node == SunburstControl.NoNode)
        {
            InfoTitle = "Double-clic ou molette vers l'avant : entrer dans un dossier. Clic au centre, molette vers l'arrière ou Retour arrière : remonter.";
            InfoPath = "";
            return;
        }
        long size = Tree.Size(node);
        InfoTitle = $"{Tree.Name(node)} — {SizeFormat.Format(size)} — {EntryRow.Percent(size, Tree.Size(Current))} de {CurrentName}";
        InfoPath = Tree.GetPath(node);
    }

    // ---------------------------------------------------------------- actions sur les éléments

    // Élément « choisi » : le dernier sélectionné par l'utilisateur, dans le diagramme ou dans une des listes.
    // C'est la cible des boutons Aperçu, Ajouter au panier et Ouvrir dans l'Explorateur.
    int _focusNode = SunburstControl.NoNode;
    string _focusText = "Aucun élément choisi";
    public bool HasFocus => Tree is not null && _focusNode != SunburstControl.NoNode && !Tree.IsRemoved(_focusNode);
    public string FocusText { get => _focusText; private set => Set(ref _focusText, value); }

    void SetFocus(int node)
    {
        _focusNode = node;
        FocusText = HasFocus ? $"{Tree!.Name(node)} — {SizeFormat.Format(Tree.Size(node))}" : "Aucun élément choisi";
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Paramètre de commande : un identifiant, un tableau d'identifiants, ou rien (élément choisi).</summary>
    int[] Targets(object? parameter) => parameter switch
    {
        _ when Tree is null => [],
        int id => [id],
        int[] ids => ids,
        _ => HasFocus ? [_focusNode] : [],
    };

    int? Single(object? parameter) => Targets(parameter) is [var id, ..] ? id : null;

    void Reveal(int node)
    {
        try
        {
            ShellActions.RevealInExplorer(Tree!.GetPath(node));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("Impossible d'ouvrir l'Explorateur : " + ErrorText.Describe(e) + ".", "Camembert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void CopyPaths(object? parameter)
    {
        var targets = Targets(parameter);
        if (targets.Length == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, targets.Select(Tree!.GetPath)));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show("Le presse-papiers est occupé par une autre application. Réessayez.", "Camembert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- réglages

    public string[] ThemeChoices { get; } = ["Selon Windows", "Clair", "Sombre"];

    public int ThemeIndex
    {
        get => (int)_settings.Theme;
        set
        {
            if (value < 0 || value == (int)_settings.Theme) return;
            SaveSettings(_settings with { Theme = (AppTheme)value });
            ThemeService.Apply(_settings.Theme);
        }
    }

    public bool AnimationsEnabled
    {
        get => _settings.Animations;
        set { if (value != _settings.Animations) SaveSettings(_settings with { Animations = value }); }
    }

    string _settingsLocation, _settingsWarning = "";
    public string SettingsLocation { get => _settingsLocation; private set => Set(ref _settingsLocation, value); }
    public string SettingsWarning { get => _settingsWarning; private set => Set(ref _settingsWarning, value); }

    void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        Raise(nameof(ThemeIndex));
        Raise(nameof(AnimationsEnabled));
        // Le réglage s'applique quoi qu'il arrive ; seul son enregistrement peut échouer, et c'est dit.
        SettingsWarning = _settingsStore.Save(settings) is { } error
            ? $"Le dossier de Camembert n'est pas inscriptible ({error}) : ce réglage vaut pour cette session seulement."
            : "";
        if (SettingsWarning.Length > 0) Log.Warning("Réglages non enregistrés : {Error}", SettingsWarning);
    }

    // ---------------------------------------------------------------- aperçu

    void ShowPreview(object? parameter)
    {
        if (Single(parameter) is not int id) return;
        SetFocus(id);
        Preview.Show(Tree!.GetPath(id));
        SelectedTab = 2;
    }

    // ---------------------------------------------------------------- panier et suppression

    string _basketSummary = "", _basketMessage = "";
    bool _isDeleting;
    public string BasketSummary { get => _basketSummary; private set => Set(ref _basketSummary, value); }
    /// <summary>Compte rendu de la dernière action sur le panier (ajout refusé, suppression effectuée…).</summary>
    public string BasketMessage { get => _basketMessage; private set => Set(ref _basketMessage, value); }
    public bool HasBasketItems => _basket is { Items.Count: > 0 };
    public bool IsDeleting { get => _isDeleting; private set { if (Set(ref _isDeleting, value)) CommandManager.InvalidateRequerySuggested(); } }

    static string Plural(int n, string singular, string plural) => $"{SizeFormat.Count(n)} {(n > 1 ? plural : singular)}";

    void RefreshBasket(string message)
    {
        BasketMessage = message;
        if (Tree is not { } tree || _basket is not { } basket)
        {
            BasketList.Load(null, [], null);
            BasketSummary = "";
        }
        else
        {
            var ids = basket.Items.ToArray();
            TreeSearch.Sort(tree, ids, BasketList.Key, BasketList.Descending);
            long total = tree.Size(FileTree.Root);
            BasketList.Load(tree, ids, id => new EntryRow(tree, id, total, Palette.SmallItemBrush, showLocation: true));
            BasketSummary = ids.Length == 0
                ? "Panier vide. Choisissez un élément puis « Ajouter au panier » : rien n'est supprimé tant que vous ne l'avez pas confirmé."
                : $"Panier : {Plural(ids.Length, "élément", "éléments")}, {SizeFormat.Format(basket.TotalSize)} récupérables une fois la Corbeille vidée.";
        }
        Raise(nameof(HasBasketItems));
        CommandManager.InvalidateRequerySuggested();
    }

    void AddToBasket(object? parameter)
    {
        if (Tree is not { } tree || _basket is not { } basket) return;
        int added = 0, already = 0;
        var refused = new List<string>();
        foreach (int id in Targets(parameter))
        {
            if (tree.IsRemoved(id)) continue;
            if (_rules.BlockReason(tree.GetPath(id)) is { } reason) refused.Add($"{tree.Name(id)} : {reason}");
            else if (basket.Add(id)) added++;
            else already++;
        }
        var parts = new List<string>();
        if (added > 0) parts.Add(Plural(added, "élément ajouté au panier.", "éléments ajoutés au panier."));
        if (already > 0) parts.Add(Plural(already, "élément déjà dans le panier (lui-même ou son dossier).", "éléments déjà dans le panier (eux-mêmes ou leur dossier)."));
        if (refused.Count > 0) parts.Add("Emplacement protégé, non ajouté — " + string.Join(" ; ", refused.Take(3)) + (refused.Count > 3 ? $" ; et {refused.Count - 3} autres" : ""));
        RefreshBasket(string.Join(" ", parts));
    }

    void RemoveFromBasket(object? parameter)
    {
        if (_basket is not { } basket) return;
        foreach (int id in Targets(parameter)) basket.Remove(id);
        RefreshBasket("");
    }

    async Task DeleteBasketAsync()
    {
        if (Tree is not { } tree || _basket is not { } basket || basket.Items.Count == 0) return;
        var ids = basket.Items.ToArray();
        var requests = ids.Select(id => new DeleteRequest(id, tree.GetPath(id), tree.IsDirectory(id), tree.Size(id), tree.ModifiedTicks(id))).ToList();

        // Confirmation explicite, « Non » par défaut.
        var lines = requests.Take(8).Select(r => "   " + r.Path).ToList();
        if (requests.Count > 8) lines.Add($"   … et {requests.Count - 8} autres");
        string question = $"Envoyer {Plural(ids.Length, "élément", "éléments")} ({SizeFormat.Format(basket.TotalSize)}) à la Corbeille Windows ?\n\n" +
                          string.Join("\n", lines) +
                          "\n\nVous pourrez les restaurer depuis la Corbeille. Rien n'est supprimé définitivement par Camembert.";
        var window = Application.Current.MainWindow;
        if (MessageBox.Show(window, question, "Camembert — confirmer la suppression", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Dossiers parents encore identiques à l'analyse : après notre propre suppression, leur nouvelle date sera
        // mémorisée, sinon ils passeraient pour « modifiés depuis l'analyse » et ne pourraient plus être supprimés.
        var unchangedParents = new HashSet<int>();
        foreach (int id in ids)
        {
            int parent = tree.Parent(id);
            if (DiskModifiedTicks(tree.GetPath(parent)) is long ticks && ticks == tree.ModifiedTicks(parent)) unchangedParents.Add(parent);
        }

        IsDeleting = true;
        BasketMessage = "Envoi à la Corbeille en cours…";
        List<DeleteResult> results;
        try
        {
            results = await SafeDelete.SendToRecycleBinAsync(requests, _rules, new System.Windows.Interop.WindowInteropHelper(window).Handle);
        }
        catch (Exception e)
        {
            IsDeleting = false;
            Log.Error(e, "Échec de l'envoi à la Corbeille");
            RefreshBasket("La suppression a échoué : " + ErrorText.Describe(e) + ".");
            return;
        }
        IsDeleting = false;
        if (!ReferenceEquals(tree, Tree)) return; // une nouvelle analyse a remplacé l'arbre entre-temps

        long freed = 0;
        int deleted = 0;
        foreach (var result in results.Where(r => r.Deleted))
        {
            int id = result.Request.Id, parent = tree.Parent(id);
            freed += tree.Size(id);
            deleted++;
            tree.Remove(id);
            basket.Remove(id);
            if (unchangedParents.Contains(parent) && DiskModifiedTicks(tree.GetPath(parent)) is long ticks) tree.SetModifiedTicks(parent, ticks);
        }

        // Tailles à jour partout, sans nouvelle analyse.
        int current = Current;
        while (current > FileTree.Root && tree.IsRemoved(current)) current = tree.Parent(current);
        Navigate(current);
        ScheduleSearch();
        foreach (var result in results)
        {
            if (result.Deleted) Log.Information("Envoyé à la Corbeille : {Path}", result.Request.Path);
            else Log.Warning("Non supprimé : {Path} — {Problem}", result.Request.Path, result.Problem);
        }
        var failures = results.Where(r => !r.Deleted).ToList();
        string report = deleted > 0
            ? $"{Plural(deleted, "élément envoyé", "éléments envoyés")} à la Corbeille ({SizeFormat.Format(freed)}). La Corbeille est sur le même disque : l'espace sera libéré quand vous la viderez."
            : "Rien n'a été supprimé.";
        if (failures.Count > 0) report += $" {Plural(failures.Count, "élément non supprimé, resté", "éléments non supprimés, restés")} dans le panier.";
        RefreshBasket(report);
        if (failures.Count > 0)
        {
            var details = failures.Take(12).Select(f => $"{f.Request.Path}\n   {f.Problem}").ToList();
            if (failures.Count > 12) details.Add($"… et {failures.Count - 12} autres");
            MessageBox.Show(window, report + "\n\n" + string.Join("\n\n", details), "Camembert — éléments non supprimés", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    static long? DiskModifiedTicks(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetLastWriteTimeUtc(directory).ToFileTimeUtc() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------------------------------------------------------------- recherche

    public string[] CategoryChoices { get; } =
        ["Toutes les catégories", .. Enum.GetValues<FileCategory>().Select(FileTypes.Label)];
    public string[] KindChoices { get; } = ["Fichiers et dossiers", "Fichiers", "Dossiers"];

    string _searchName = "", _searchExtensions = "", _searchMinSize = "", _searchMaxSize = "", _searchSummary = "";
    int _searchCategory, _searchKind;

    public string SearchName
    {
        get => _searchName;
        set
        {
            if (!Set(ref _searchName, value)) return;
            if (value.Length > 0) SelectedTab = 1; // saisie depuis la barre supérieure : montrer les résultats
            ScheduleSearch();
        }
    }
    public string SearchExtensions { get => _searchExtensions; set { if (Set(ref _searchExtensions, value)) ScheduleSearch(); } }
    public string SearchMinSize { get => _searchMinSize; set { if (Set(ref _searchMinSize, value)) ScheduleSearch(); } }
    public string SearchMaxSize { get => _searchMaxSize; set { if (Set(ref _searchMaxSize, value)) ScheduleSearch(); } }
    public int SearchCategory { get => _searchCategory; set { if (Set(ref _searchCategory, value)) ScheduleSearch(); } }
    public int SearchKind { get => _searchKind; set { if (Set(ref _searchKind, value)) ScheduleSearch(); } }
    public string SearchSummary { get => _searchSummary; private set => Set(ref _searchSummary, value); }

    void ClearSearch()
    {
        SearchName = SearchExtensions = SearchMinSize = SearchMaxSize = "";
        SearchCategory = SearchKind = 0;
    }

    /// <summary>Relance la recherche un instant après la dernière frappe.</summary>
    void ScheduleSearch()
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    async void RunSearch()
    {
        _searchCancellation?.Cancel();
        if (Tree is not { } tree || _search is not { } search) return;

        long minSize = 0, maxSize = long.MaxValue;
        foreach (var (text, isMin) in new[] { (SearchMinSize, true), (SearchMaxSize, false) })
        {
            if (text.Trim().Length == 0) continue;
            if (!SizeFormat.TryParse(text, out long bytes))
            {
                SearchList.Load(null, [], null);
                SearchSummary = $"Taille non reconnue : « {text.Trim()} ». Exemples : 500 Mo, 1,5 Go, 200 Ko.";
                return;
            }
            if (isMin) minSize = bytes; else maxSize = bytes;
        }
        var query = new SearchQuery(SearchName, SearchExtensions,
            SearchCategory == 0 ? null : (FileCategory)(SearchCategory - 1), minSize, maxSize, (EntryKind)SearchKind);
        if (query.IsEmpty)
        {
            SearchList.Load(null, [], null);
            SearchSummary = "Saisissez un nom, une extension ou une taille : la recherche porte sur toute l'analyse, sans relire le disque.";
            return;
        }

        var cancellation = _searchCancellation = new CancellationTokenSource();
        var (key, descending) = (SearchList.Key, SearchList.Descending);
        var clock = Stopwatch.StartNew();
        try
        {
            var ids = await Task.Run(() =>
            {
                var found = search.Find(query, cancellation.Token);
                TreeSearch.Sort(tree, found, key, descending);
                return found;
            });
            if (cancellation.IsCancellationRequested || !ReferenceEquals(tree, Tree)) return;
            long total = tree.Size(FileTree.Root);
            SearchList.Load(tree, ids, id => new EntryRow(tree, id, total, Palette.SmallItemBrush, showLocation: true));
            SearchSummary = $"{SizeFormat.Count(ids.Length)} {(ids.Length > 1 ? "résultats" : "résultat")} en {clock.ElapsedMilliseconds} ms. Double-clic : afficher l'élément dans son dossier.";
        }
        catch (OperationCanceledException)
        {
            // Remplacée par une recherche plus récente.
        }
    }
}
