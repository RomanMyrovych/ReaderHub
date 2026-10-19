using System.Windows;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookHub.App.Views;

public partial class MetadataEditorViewModel : ObservableObject
{
    private readonly LibraryDatabase _database;
    private readonly List<BookRecord> _targetBooks;

    public bool IsSingleBookMode => _targetBooks.Count == 1;
    public string DialogTitle => IsSingleBookMode ? $"Редактор метаданих — {_targetBooks[0].Title}" : $"Пакетне редагування ({_targetBooks.Count} книг)";
    public string HeaderText => IsSingleBookMode ? "Редагування метаданих та кодування книги" : $"Пакетне редагування ({_targetBooks.Count} виділених книг)";
    public string SubHeaderText => IsSingleBookMode
        ? $"Файл: {_targetBooks[0].FilePath}"
        : "Заповнені поля будуть застосовані до всіх виділених книг. Порожні поля залишаться без змін.";

    public string[] AvailableEncodings => EncodingAndTagWriterService.SupportedEncodingNames;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _author = string.Empty;
    [ObservableProperty] private string _publishYearText = string.Empty;
    [ObservableProperty] private string _isbn = string.Empty;
    [ObservableProperty] private string _doi = string.Empty;
    [ObservableProperty] private string _publisher = string.Empty;
    [ObservableProperty] private string _language = "uk";
    [ObservableProperty] private string _series = string.Empty;
    [ObservableProperty] private string _volumeNumberText = string.Empty;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _detectedEncodingInfo = string.Empty;
    [ObservableProperty] private string _selectedSourceEncoding = "windows-1251";
    [ObservableProperty] private bool _writeTagsToFileOnSave;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public MetadataEditorViewModel(LibraryDatabase database, IEnumerable<BookRecord> books)
    {
        _database = database;
        _targetBooks = books.ToList();

        if (_targetBooks.Count == 1)
        {
            var b = _targetBooks[0];
            Title = b.Title;
            Author = b.Author;
            PublishYearText = b.PublishYear?.ToString() ?? string.Empty;
            Isbn = b.Isbn ?? string.Empty;
            Doi = b.Doi ?? string.Empty;
            Publisher = b.Publisher ?? string.Empty;
            Language = b.Language ?? "uk";
            Series = b.Series ?? string.Empty;
            VolumeNumberText = b.VolumeNumber?.ToString() ?? string.Empty;
            Category = b.Category;
            Tags = b.Tags;
            Description = b.Description ?? string.Empty;
            DetectedEncodingInfo = $"Поточне визначене кодування: {b.DetectedEncoding ?? "UTF-8"}";
            WriteTagsToFileOnSave = b.Extension is ".fb2" or ".epub";
        }
    }

    [RelayCommand]
    private void ReEncodeToUtf8()
    {
        if (_targetBooks.Count != 1) return;
        var b = _targetBooks[0];
        try
        {
            string bak = EncodingAndTagWriterService.ConvertFileToCleanUtf8WithBackup(b.FilePath, SelectedSourceEncoding);
            b.DetectedEncoding = "UTF-8";
            DetectedEncodingInfo = $"Перекодовано у чистий UTF-8! Створено резервну копію: {bak}";
            FeedbackMessage = "Файл успішно конвертовано у чистий UTF-8.";
        }
        catch (Exception ex)
        {
            FeedbackMessage = $"Помилка перекодування: {ex.Message}";
        }
    }

    public async Task SaveChangesAsync()
    {
        if (IsSingleBookMode)
        {
            var b = _targetBooks[0];
            b.Title = string.IsNullOrWhiteSpace(Title) ? b.Title : Title.Trim();
            b.Author = string.IsNullOrWhiteSpace(Author) ? "Невідомий автор" : Author.Trim();
            b.PublishYear = int.TryParse(PublishYearText, out int yr) ? yr : null;
            b.Isbn = string.IsNullOrWhiteSpace(Isbn) ? null : Isbn.Trim();
            b.Doi = string.IsNullOrWhiteSpace(Doi) ? null : Doi.Trim();
            b.Publisher = string.IsNullOrWhiteSpace(Publisher) ? null : Publisher.Trim();
            b.Language = string.IsNullOrWhiteSpace(Language) ? "uk" : Language.Trim();
            b.Series = string.IsNullOrWhiteSpace(Series) ? null : Series.Trim();
            b.VolumeNumber = int.TryParse(VolumeNumberText, out int vol) ? vol : null;
            b.Category = string.IsNullOrWhiteSpace(Category) ? "Загальне" : Category.Trim();
            b.Tags = Tags?.Trim() ?? string.Empty;
            b.Description = Description?.Trim();

            await _database.UpdateBookFullMetadataAsync(b);

            if (WriteTagsToFileOnSave)
            {
                EncodingAndTagWriterService.TryWriteMetadataToFile(b, out string msg);
                FeedbackMessage = msg;
            }
        }
        else
        {
            await _database.BatchUpdateBooksAsync(
                _targetBooks.Select(x => x.Id),
                Author,
                Category,
                Tags,
                null,
                Series);
        }
    }
}

public partial class MetadataEditorWindow : Window
{
    private readonly MetadataEditorViewModel _vm;

    public MetadataEditorWindow(MetadataEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SaveChangesAsync();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
