using System.Windows;
using Qplus.Core.Sync;

namespace Qplus.App.Views;

public partial class SyncSettingsDialog : Window
{
    private readonly QuerySyncService _sync;
    private const string KeyPlaceholder = "••••••••";
    private readonly bool _hadKey;

    public SyncSettingsDialog(QuerySyncService sync)
    {
        InitializeComponent();
        _sync = sync;

        UrlBox.Text = sync.ServerUrl;
        _hadKey = !string.IsNullOrEmpty(sync.ApiKey);
        KeyBox.Password = _hadKey ? KeyPlaceholder : "";
        StatusText.Text = sync.LastSyncUtc is { } t
            ? $"Last synced {t.ToLocalTime():yyyy-MM-dd HH:mm}."
            : "Not synced yet.";
    }

    /// <summary>The key as typed, or the stored one when the user left the placeholder alone.</summary>
    private string EffectiveKey =>
        _hadKey && KeyBox.Password == KeyPlaceholder ? _sync.ApiKey : KeyBox.Password;

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Testing…";
        var (ok, message) = await _sync.TestAsync(UrlBox.Text, EffectiveKey, CancellationToken.None);
        StatusText.Text = message;
        MessageBox.Show(this, message, ok ? "Server reachable" : "Cannot reach server",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        UrlBox.Text = QuerySyncService.DefaultServerUrl;
        StatusText.Text = "Reset to the default server address.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            StatusText.Text = "Server address is required.";
            return;
        }

        var previousUrl = _sync.ServerUrl;
        _sync.ServerUrl = UrlBox.Text;
        _sync.ApiKey = EffectiveKey;

        // Pointing at a different server means the old watermark is meaningless — clear it
        // so the next sync exchanges the full library rather than silently missing rows.
        if (!string.Equals(previousUrl, _sync.ServerUrl, StringComparison.OrdinalIgnoreCase))
            _sync.ResetWatermark();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
