using System.Windows;
using System.Windows.Threading;
using Qplus.Core.Storage;

namespace Qplus.App;

public partial class App : Application
{
    public const string ThemeSettingKey = "theme";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the persisted theme before the main window renders.
        try
        {
            var store = new CatalogStore();
            ThemeManager.Apply(ThemeManager.Parse(store.GetSetting(ThemeSettingKey)));
        }
        catch
        {
            ThemeManager.Apply(AppTheme.Dark);
        }
        SqlSyntax.Apply(ThemeManager.Current);

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "Qplus — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
