using System.Windows;
using System.Windows.Controls;
using Qplus.Core.Data;
using Qplus.Core.Models;
using Qplus.Core.Security;

namespace Qplus.App.Views;

public partial class ConnectionDialog : Window
{
    private sealed record EngineItem(DbEngineKind Kind, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>The edited connection, valid only after the dialog returns true.</summary>
    public ConnectionInfo Result { get; }

    private readonly bool _hasExistingPassword;

    public ConnectionDialog(ConnectionInfo? existing)
    {
        InitializeComponent();

        Result = existing?.Clone() ?? new ConnectionInfo();
        _hasExistingPassword = !string.IsNullOrEmpty(Result.EncryptedPassword);

        EngineBox.ItemsSource = DbEngines.All
            .Select(e => new EngineItem(e.Kind, e.DisplayName))
            .ToList();

        LoadFromModel();
    }

    private void LoadFromModel()
    {
        NameBox.Text = Result.Name;
        EngineBox.SelectedItem = ((IEnumerable<EngineItem>)EngineBox.ItemsSource)
            .First(i => i.Kind == Result.Engine);
        HostBox.Text = Result.Host;
        PortBox.Text = Result.Port > 0 ? Result.Port.ToString() : "";
        DbBox.Text = Result.Database;
        OracleSidBox.IsChecked = Result.OracleUseSid;
        IntegratedBox.IsChecked = Result.IntegratedSecurity;
        UserBox.Text = Result.Username;
        PwdBox.Password = _hasExistingPassword ? "••••••••" : "";
        TrustCertBox.IsChecked = Result.TrustServerCertificate;
        UpdateEngineVisibility();
    }

    private DbEngineKind SelectedEngine =>
        (EngineBox.SelectedItem as EngineItem)?.Kind ?? DbEngineKind.SqlServer;

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateEngineVisibility();

    private void IntegratedBox_Toggled(object sender, RoutedEventArgs e)
        => UpdateEngineVisibility();

    private void UpdateEngineVisibility()
    {
        var isOracle = SelectedEngine == DbEngineKind.Oracle;
        var isSql = !isOracle;

        OracleSidBox.Visibility = isOracle ? Visibility.Visible : Visibility.Collapsed;
        IntegratedBox.Visibility = isSql ? Visibility.Visible : Visibility.Collapsed;
        TrustCertBox.Visibility = isSql ? Visibility.Visible : Visibility.Collapsed;
        DbLabel.Text = isOracle ? "Service / SID" : "Database";

        var integrated = isSql && IntegratedBox.IsChecked == true;
        UserBox.IsEnabled = !integrated;
        PwdBox.IsEnabled = !integrated;
    }

    private bool ApplyToModel()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            StatusText.Text = "Name is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            StatusText.Text = "Host is required.";
            return false;
        }

        Result.Name = NameBox.Text.Trim();
        Result.Engine = SelectedEngine;
        Result.Host = HostBox.Text.Trim();
        Result.Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : 0;
        Result.Database = DbBox.Text.Trim();
        Result.OracleUseSid = OracleSidBox.IsChecked == true;
        Result.IntegratedSecurity = SelectedEngine == DbEngineKind.SqlServer && IntegratedBox.IsChecked == true;
        Result.Username = UserBox.Text.Trim();
        Result.TrustServerCertificate = TrustCertBox.IsChecked == true;

        // Only re-encrypt if the user actually changed the password field.
        var pwd = PwdBox.Password;
        if (!(_hasExistingPassword && pwd == "••••••••"))
            Result.EncryptedPassword = SecretProtector.Protect(pwd);

        return true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyToModel()) return;
        StatusText.Text = "Testing…";
        var (ok, msg) = await QueryRunner.TestAsync(Result, CancellationToken.None);
        StatusText.Text = ok ? "OK" : "Failed";
        MessageBox.Show(this, msg, ok ? "Connection succeeded" : "Connection failed",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyToModel()) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
