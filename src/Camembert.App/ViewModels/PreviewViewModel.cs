using System.Windows.Input;
using System.Windows.Media;
using Camembert.Windows.Preview;
using Camembert.Core.Services;

namespace Camembert.App.ViewModels;

/// <summary>Aperçu de l'élément choisi : chargé à la demande, en arrière-plan ; le dernier demandé l'emporte.</summary>
public sealed class PreviewViewModel : ViewModelBase
{
    CancellationTokenSource? _loading;
    PdfPages? _pdf;
    int _page;

    public PreviewViewModel()
    {
        PreviousPageCommand = new RelayCommand(_ => ShowPage(_page - 1), () => _pdf is not null && _page > 0);
        NextPageCommand = new RelayCommand(_ => ShowPage(_page + 1), () => _pdf is not null && _page < _pdf.PageCount - 1);
    }

    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }

    string _title = "Aucun élément choisi", _note = "Sélectionnez un élément puis « Aperçu » (ou la touche Espace dans une liste).", _pageText = "";
    List<KeyValuePair<string, string>> _details = [];
    ImageSource? _image;
    string? _text;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public List<KeyValuePair<string, string>> Details { get => _details; private set => Set(ref _details, value); }
    public string Note { get => _note; private set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;
    public ImageSource? Image { get => _image; private set { if (Set(ref _image, value)) Raise(nameof(HasImage)); } }
    public bool HasImage => _image is not null;
    public string? Text { get => _text; private set { if (Set(ref _text, value)) Raise(nameof(HasText)); } }
    public bool HasText => _text is not null;
    public string PageText { get => _pageText; private set { if (Set(ref _pageText, value)) Raise(nameof(HasPages)); } }
    public bool HasPages => _pageText.Length > 0;

    public async void Show(string path)
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        _pdf = null;
        Title = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
        Details = [];
        Image = null;
        Text = null;
        PageText = "";
        Note = "Chargement de l'aperçu…";
        try
        {
            var content = await PreviewLoader.LoadAsync(path, loading.Token);
            if (loading.IsCancellationRequested) return;
            Details = content.Details;
            Image = content.Image;
            Text = content.Text;
            Note = content.Note ?? "";
            _pdf = content.Pdf;
            _page = 0;
            PageText = _pdf is { PageCount: > 1 } ? $"Page 1 sur {_pdf.PageCount}" : "";
            CommandManager.InvalidateRequerySuggested();
        }
        catch (OperationCanceledException)
        {
            // Un autre élément a été demandé entre-temps.
        }
    }

    async void ShowPage(int page)
    {
        if (_pdf is not { } pdf || page < 0 || page >= pdf.PageCount) return;
        try
        {
            var image = await pdf.RenderAsync(page);
            if (!ReferenceEquals(pdf, _pdf)) return;
            _page = page;
            Image = image;
            PageText = $"Page {page + 1} sur {pdf.PageCount}";
            Note = "";
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Note = "Cette page ne peut pas être affichée : " + ErrorText.Describe(e);
        }
        CommandManager.InvalidateRequerySuggested();
    }
}
