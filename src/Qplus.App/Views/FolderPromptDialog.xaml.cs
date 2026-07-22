using System.Windows;

namespace Qplus.App.Views;

/// <summary>Asks for a folder — an existing one or a newly typed name.</summary>
public partial class FolderPromptDialog : Window
{
    /// <summary>Chosen folder; empty means "no folder".</summary>
    public string Folder => (FolderBox.Text ?? "").Trim().Trim('/');

    public FolderPromptDialog(IEnumerable<string> existingFolders, string current = "")
    {
        InitializeComponent();

        FolderBox.ItemsSource = existingFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FolderBox.Text = current ?? "";

        Loaded += (_, _) => FolderBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
