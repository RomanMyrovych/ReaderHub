using System.Windows;
using System.Windows.Controls;
using BookHub.Core.Data;
using BookHub.Core.Models;

namespace BookHub.App.Views;

public partial class DuplicatesWindow : Window
{
    private readonly LibraryDatabase _database;
    private readonly Func<Task> _onMergedCallback;

    public DuplicatesWindow(LibraryDatabase database, Func<Task> onMergedCallback)
    {
        InitializeComponent();
        _database = database;
        _onMergedCallback = onMergedCallback;
        Loaded += async (_, _) => await RefreshGroupsAsync();
    }

    private async Task RefreshGroupsAsync()
    {
        var groups = await _database.FindDuplicatesAsync();
        GroupsListBox.ItemsSource = groups;
        StatusText.Text = groups.Count == 0
            ? "✅ Дублікатів у бібліотеці не виявлено."
            : $"Виявлено груп потенційних дублікатів: {groups.Count}";
    }

    private async void MergeGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DuplicateGroup group && group.Books.Count >= 2)
        {
            var primary = group.Books[0];
            var dupIds = group.Books.Skip(1).Select(b => b.Id);
            await _database.MergeDuplicateBooksAsync(primary.Id, dupIds);
            await RefreshGroupsAsync();
            await _onMergedCallback();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
