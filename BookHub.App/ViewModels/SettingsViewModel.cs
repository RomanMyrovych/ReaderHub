using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BookHub.App.Services;
using BookHub.Core.Data;
using BookHub.Core.Models;
using BookHub.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BookHub.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly LibraryDatabase _database;
    private readonly FileScannerService _scanner;
    private readonly UserSettingsService _settingsService;
    private readonly Func<Task> _onFoldersChangedCallback;

    public ObservableCollection<RootFolderRecord> RootFolders { get; } = [];
    public IReadOnlyList<LanguageOption> SupportedLanguages => LocalizationService.SupportedLanguages;

    [ObservableProperty]
    private LanguageOption _selectedLanguageOption;

    [ObservableProperty]
    private string _newFolderPathInput = string.Empty;

    [ObservableProperty]
    private string _databasePathInfo = string.Empty;

    [ObservableProperty]
    private string _cachePathInfo = string.Empty;

    public SettingsViewModel(
        LibraryDatabase database,
        FileScannerService scanner,
        UserSettingsService settingsService,
        Func<Task> onFoldersChangedCallback)
    {
        _database = database;
        _scanner = scanner;
        _settingsService = settingsService;
        _onFoldersChangedCallback = onFoldersChangedCallback;
        DatabasePathInfo = _database.DatabasePath;
        CachePathInfo = _database.CacheDirectory;

        string currentCode = _settingsService.Current.LanguageCode ?? "uk";
        _selectedLanguageOption = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
                                  ?? SupportedLanguages[0];
    }

    partial void OnSelectedLanguageOptionChanged(LanguageOption value)
    {
        if (value == null) return;
        LocalizationService.ApplyLanguage(value.Code);
        _settingsService.Current.LanguageCode = value.Code;
        _settingsService.Save();
    }

    public async Task LoadFoldersAsync()
    {
        var folders = await _database.GetRootFoldersAsync();
        RootFolders.Clear();
        foreach (var f in folders)
        {
            RootFolders.Add(f);
        }
    }

    [RelayCommand]
    private async Task BrowseAndAddFolderAsync()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Оберіть кореневу папку з книгами та документами",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var folder in dlg.FolderNames)
            {
                if (Directory.Exists(folder))
                {
                    await _database.AddRootFolderAsync(folder);
                }
            }

            await LoadFoldersAsync();
            await _scanner.RefreshWatchersAsync();
            await _onFoldersChangedCallback();
        }
    }

    [RelayCommand]
    private async Task AddManualFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFolderPathInput)) return;
        string path = NewFolderPathInput.Trim();

        if (!Directory.Exists(path))
        {
            MessageBox.Show($"Папка не існує:\n{path}", "Увага", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _database.AddRootFolderAsync(path);
        NewFolderPathInput = string.Empty;
        await LoadFoldersAsync();
        await _scanner.RefreshWatchersAsync();
        await _onFoldersChangedCallback();
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(RootFolderRecord? folder)
    {
        if (folder == null) return;
        await _database.RemoveRootFolderAsync(folder.Id, folder.Path);
        await LoadFoldersAsync();
        await _scanner.RefreshWatchersAsync();
        await _onFoldersChangedCallback();
    }
}
