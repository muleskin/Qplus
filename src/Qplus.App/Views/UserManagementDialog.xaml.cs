using System.Data.Common;
using System.Windows;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.Views;

public partial class UserManagementDialog : Window
{
    private readonly ConnectionInfo _conn;
    private readonly IDbEngine _engine;

    /// <summary>The accumulated DDL to open in the editor; null if the user closed without opening.</summary>
    public string? GeneratedSql { get; private set; }

    public UserManagementDialog(ConnectionInfo conn)
    {
        InitializeComponent();
        _conn = conn;
        _engine = DbEngines.For(conn);
        HeaderText.Text = $"{conn.Name} — {_engine.DisplayName}";
        Loaded += async (_, _) => await LoadListsAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadListsAsync();

    private async Task LoadListsAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            await using var open = _engine.CreateConnection(_engine.BuildConnectionString(_conn));
            await open.OpenAsync();
            UsersList.ItemsSource = await ReadColumnAsync(open, _engine.ListUsersSql());
            RolesList.ItemsSource = await ReadColumnAsync(open, _engine.ListRolesSql());
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed";
            MessageBox.Show(this, ex.Message, "User Management",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static async Task<List<string>> ReadColumnAsync(DbConnection open, string sql)
    {
        var list = new List<string>();
        await using var cmd = open.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(r.IsDBNull(0) ? "" : r.GetValue(0).ToString() ?? "");
        return list;
    }

    private void Append(string sql)
    {
        if (PreviewBox.Text.Length > 0 && !PreviewBox.Text.EndsWith('\n'))
            PreviewBox.AppendText("\n");
        PreviewBox.AppendText(sql + "\n");
        PreviewBox.ScrollToEnd();
    }

    private void CreateUser_Click(object sender, RoutedEventArgs e)
    {
        var user = NewUserBox.Text.Trim();
        var pwd = NewPwdBox.Password;
        if (string.IsNullOrEmpty(user)) { StatusText.Text = "Enter a user name."; return; }
        if (string.IsNullOrEmpty(pwd)) { StatusText.Text = "Enter a password."; return; }
        Append(_engine.BuildCreateUserSql(user, pwd));
        StatusText.Text = $"Added CREATE for '{user}'.";
    }

    private void GrantRole_Click(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is not string user) { StatusText.Text = "Select a user."; return; }
        if (RolesList.SelectedItem is not string role) { StatusText.Text = "Select a role."; return; }
        Append(_engine.BuildGrantRoleSql(role, user));
        StatusText.Text = $"Added GRANT {role} → {user}.";
    }

    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is not string user)
        {
            StatusText.Text = "Select a user to edit.";
            return;
        }

        var dlg = new EditUserDialog(_conn, user) { Owner = this };
        dlg.ShowDialog();
        await LoadListsAsync();   // roles/users may have changed
    }

    private async void NewUser_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EditUserDialog(_conn, null) { Owner = this };
        dlg.ShowDialog();
        await LoadListsAsync();
    }

    private void DropUser_Click(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is not string user) { StatusText.Text = "Select a user."; return; }
        Append(_engine.BuildDropUserSql(user));
        StatusText.Text = $"Added DROP for '{user}'.";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreviewBox.Text))
        {
            StatusText.Text = "Nothing to open — build some DDL first.";
            return;
        }
        GeneratedSql = "-- Review carefully before running (user administration).\n" + PreviewBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
