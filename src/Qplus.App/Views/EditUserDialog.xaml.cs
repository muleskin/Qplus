using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Qplus.Core.Admin;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.Views;

/// <summary>
/// SQL-Developer-style Edit User dialog: account settings, granted roles, system
/// privileges, tablespace quotas, and a live preview of the DDL that Apply will run.
/// </summary>
public partial class EditUserDialog : Window
{
    private readonly ConnectionInfo _conn;
    private readonly IUserAdmin _admin;
    private readonly bool _isNew;

    private UserEditModel _model = new();
    private ICollectionView? _rolesView;
    private ICollectionView? _privsView;

    public EditUserDialog(ConnectionInfo conn, string? userName)
    {
        InitializeComponent();
        _conn = conn;
        _admin = UserAdmins.For(conn);
        _isNew = string.IsNullOrWhiteSpace(userName);

        Title = _isNew ? "Create User" : $"Edit User — {userName}";
        UserNameBox.Text = userName ?? "";
        UserNameBox.IsReadOnly = !_isNew;

        if (!_admin.SupportsTablespaces)
        {
            // SQL Server: no tablespaces or quotas.
            DefaultSpaceLabel.Text = "Default Schema";
            TempSpaceLabel.Visibility = Visibility.Collapsed;
            TempSpaceBox.Visibility = Visibility.Collapsed;
            EditionsBox.Visibility = Visibility.Collapsed;
            QuotasTab.Visibility = Visibility.Collapsed;
            ExternalBox.Content = "Windows (integrated) login";
        }

        Loaded += async (_, _) => await LoadAsync();
    }

    // ================= Loading =================

    private async Task LoadAsync()
    {
        SetStatus("Loading…");
        try
        {
            var engine = DbEngines.For(_conn);
            await using var open = engine.CreateConnection(engine.BuildConnectionString(_conn));
            await open.OpenAsync();

            var name = UserNameBox.Text.Trim();
            var details = _isNew ? new UserDetails { Name = name } : await _admin.GetUserAsync(open, name, CancellationToken.None);

            if (details is null)
            {
                SetStatus($"User '{name}' not found.");
                return;
            }

            var roles = await _admin.GetRolesAsync(open, name, CancellationToken.None);
            var privs = await _admin.GetPrivilegesAsync(open, name, CancellationToken.None);
            var quotas = _admin.SupportsTablespaces
                ? await _admin.GetQuotasAsync(open, name, CancellationToken.None)
                : Array.Empty<TablespaceQuota>();
            var spaces = _admin.SupportsTablespaces
                ? await _admin.ListTablespacesAsync(open, CancellationToken.None)
                : Array.Empty<string>();

            _model = new UserEditModel
            {
                Details = details,
                IsNew = _isNew,
                DefaultTablespace = details.DefaultTablespace,
                TemporaryTablespace = details.TemporaryTablespace,
                Locked = details.IsLocked,
                ExternalAuth = details.IsExternalAuth,
                EditionsEnabled = details.EditionsEnabled,
                Roles = roles.ToList(),
                Privileges = privs.ToList(),
                Quotas = quotas.ToList(),
            };

            // User tab
            DefaultSpaceBox.ItemsSource = spaces;
            TempSpaceBox.ItemsSource = spaces;
            DefaultSpaceBox.Text = details.DefaultTablespace;
            TempSpaceBox.Text = details.TemporaryTablespace;
            LockedBox.IsChecked = details.IsLocked;
            ExternalBox.IsChecked = details.IsExternalAuth;
            ExpiredBox.IsChecked = details.IsPasswordExpired;
            EditionsBox.IsChecked = details.EditionsEnabled;

            // Grids (filterable views over the live model objects)
            _rolesView = CollectionViewSource.GetDefaultView(_model.Roles);
            RolesGrid.ItemsSource = _rolesView;

            _privsView = CollectionViewSource.GetDefaultView(_model.Privileges);
            PrivsGrid.ItemsSource = _privsView;

            QuotasGrid.ItemsSource = _model.Quotas;

            SetStatus(_isNew
                ? "Enter details, then Apply."
                : $"{roles.Count(r => r.Granted)} role(s), {privs.Count(p => p.Granted)} privilege(s) granted. Status: {details.AccountStatus}");
        }
        catch (Exception ex)
        {
            SetStatus("Load failed: " + ex.Message);
        }
    }

    // ================= Filtering =================

    private void RoleFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rolesView is null) return;
        var term = RoleFilterBox.Text.Trim();
        _rolesView.Filter = term.Length == 0
            ? null
            : o => o is RoleGrant r && r.Role.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void PrivFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_privsView is null) return;
        var term = PrivFilterBox.Text.Trim();
        _privsView.Filter = term.Length == 0
            ? null
            : o => o is PrivilegeGrant p && p.Privilege.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void RolesGrantAll_Click(object sender, RoutedEventArgs e) => SetAllShownRoles(true);
    private void RolesRevokeAll_Click(object sender, RoutedEventArgs e) => SetAllShownRoles(false);

    private void SetAllShownRoles(bool granted)
    {
        if (_rolesView is null) return;
        foreach (var item in _rolesView.Cast<RoleGrant>().ToList()) item.Granted = granted;
        RolesGrid.Items.Refresh();
        SetStatus(granted ? "All shown roles marked granted." : "All shown roles marked revoked.");
    }

    // ================= SQL preview =================

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedItem is TabItem { Header: "SQL" }) SqlBox.Text = BuildScript();
    }

    /// <summary>Pulls the UI state into the model and generates the DDL.</summary>
    private string BuildScript()
    {
        // Commit any in-progress grid edit so it's part of the script.
        RolesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PrivsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        QuotasGrid.CommitEdit(DataGridEditingUnit.Row, true);

        _model.Details.Name = UserNameBox.Text.Trim();
        _model.IsNew = _isNew;
        _model.NewPassword = PasswordBox1.Password;
        _model.ExpirePassword = ExpiredBox.IsChecked == true;
        _model.Locked = LockedBox.IsChecked == true;
        _model.ExternalAuth = ExternalBox.IsChecked == true;
        _model.EditionsEnabled = EditionsBox.IsChecked == true;
        _model.DefaultTablespace = DefaultSpaceBox.Text.Trim();
        _model.TemporaryTablespace = TempSpaceBox.Text.Trim();

        return _admin.BuildAlterScript(_model);
    }

    // ================= Apply =================

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserNameBox.Text))
        {
            SetStatus("User name is required.");
            return;
        }

        if (PasswordBox1.Password != PasswordBox2.Password)
        {
            SetStatus("Passwords do not match.");
            MessageBox.Show(this, "The two passwords do not match.", "Edit User",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isNew && string.IsNullOrEmpty(PasswordBox1.Password) && ExternalBox.IsChecked != true)
        {
            SetStatus("A new user needs a password (or must be an OS/Windows user).");
            return;
        }

        var script = BuildScript();
        SqlBox.Text = script;

        if (string.IsNullOrWhiteSpace(script))
        {
            SetStatus("No changes to apply.");
            return;
        }

        // User administration is high impact — always show exactly what will run.
        var confirm = MessageBox.Show(this,
            $"Run the following against {_conn.Name}?\n\n{script}",
            "Apply user changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Apply cancelled.");
            return;
        }

        SetStatus("Applying…");
        var result = await QueryRunner.ExecuteAsync(_conn, script, CancellationToken.None);

        if (result.HasError)
        {
            SetStatus("Failed: " + result.ErrorText);
            MessageBox.Show(this, result.ErrorText, "Apply failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetStatus("Applied successfully.");
        PasswordBox1.Clear();
        PasswordBox2.Clear();
        await LoadAsync();   // re-read so "was" state matches reality
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void SetStatus(string text) => StatusText.Text = text;
}
