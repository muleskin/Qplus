using System.Windows;
using Qplus.Core.Security;
using Qplus.Core.Storage;

namespace Qplus.App.Views;

/// <summary>
/// Turns query encryption on or off, unlocks a library on a new machine, and rotates the
/// passphrase. Every path that changes the key re-writes the stored queries so the library
/// is never left half-converted.
/// </summary>
public partial class EncryptionDialog : Window
{
    private readonly CatalogStore _store;
    private readonly QueryKeyRing _keys;

    /// <summary>True when the library's protection state changed and callers should reload.</summary>
    public bool Changed { get; private set; }

    public EncryptionDialog(CatalogStore store, QueryKeyRing keys)
    {
        InitializeComponent();
        _store = store;
        _keys = keys;
        Refresh();
    }

    private enum Mode { Enable, Unlock, Rotate }

    private Mode CurrentMode =>
        !_keys.IsEnabled ? Mode.Enable :
        !_keys.IsUnlocked ? Mode.Unlock : Mode.Rotate;

    private void Refresh()
    {
        PassBox.Clear();
        ConfirmBox.Clear();

        switch (CurrentMode)
        {
            case Mode.Enable:
                StatusHeader.Text = "Encryption is off";
                PassLabel.Text = "Choose a passphrase";
                ConfirmLabel.Visibility = Visibility.Visible;
                ConfirmBox.Visibility = Visibility.Visible;
                PrimaryButton.Content = "Enable";
                DisableButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "Existing queries will be encrypted in place.";
                break;

            case Mode.Unlock:
                StatusHeader.Text = "Encrypted — locked on this machine";
                PassLabel.Text = "Passphrase";
                ConfirmLabel.Visibility = Visibility.Collapsed;
                ConfirmBox.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = "Unlock";
                DisableButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "Enter the passphrase used by the rest of the library.";
                break;

            default:
                StatusHeader.Text = "Encrypted — unlocked";
                PassLabel.Text = "New passphrase";
                ConfirmLabel.Visibility = Visibility.Visible;
                ConfirmBox.Visibility = Visibility.Visible;
                PrimaryButton.Content = "Change passphrase";
                DisableButton.Visibility = Visibility.Visible;
                StatusText.Text = "Queries are protected. Changing the passphrase re-encrypts them all.";
                break;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        var pass = PassBox.Password;

        if (string.IsNullOrEmpty(pass))
        {
            StatusText.Text = "Enter a passphrase.";
            return;
        }

        switch (CurrentMode)
        {
            case Mode.Enable: EnableEncryption(pass); break;
            case Mode.Unlock: UnlockLibrary(pass); break;
            default: RotatePassphrase(pass); break;
        }
    }

    private void EnableEncryption(string pass)
    {
        if (!PassphraseAcceptable(pass)) return;
        if (pass != ConfirmBox.Password) { StatusText.Text = "The passphrases do not match."; return; }

        if (Confirm("Encrypt every saved query with this passphrase?\n\n"
                    + "If the passphrase is lost the queries cannot be recovered.") != MessageBoxResult.Yes)
            return;

        _keys.Enable(pass);
        var count = _store.ReprotectAllQueries(oldKeys: null, newKeys: _keys.Keys);
        _store.ProtectionKeys = _keys.Keys;

        Changed = true;
        StatusText.Text = $"Encryption enabled — {count} query(s) protected.";
        Refresh();
    }

    private void UnlockLibrary(string pass)
    {
        if (!_keys.Unlock(pass))
        {
            StatusText.Text = "That passphrase does not match this library.";
            return;
        }

        _store.ProtectionKeys = _keys.Keys;
        Changed = true;
        StatusText.Text = "Unlocked.";
        Refresh();
    }

    private void RotatePassphrase(string pass)
    {
        if (!PassphraseAcceptable(pass)) return;
        if (pass != ConfirmBox.Password) { StatusText.Text = "The passphrases do not match."; return; }

        if (Confirm("Re-encrypt every saved query under the new passphrase?\n\n"
                    + "Other machines will need the new passphrase after their next sync.") != MessageBoxResult.Yes)
            return;

        var oldKeys = _keys.Keys;
        var newKeys = QueryKeyRing.Derive(pass);

        var count = _store.ReprotectAllQueries(oldKeys, newKeys);
        _keys.Enable(pass);                      // stores the new verifier and caches the key
        _store.ProtectionKeys = _keys.Keys;

        Changed = true;
        StatusText.Text = $"Passphrase changed — {count} query(s) re-encrypted.";
        Refresh();
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (!_keys.IsUnlocked)
        {
            StatusText.Text = "Unlock the library before turning encryption off.";
            return;
        }

        if (Confirm("Turn off encryption and store all queries as readable text?\n\n"
                    + "Anyone with access to this catalog or the server database will be able "
                    + "to read them.") != MessageBoxResult.Yes)
            return;

        var count = _store.ReprotectAllQueries(oldKeys: _keys.Keys, newKeys: null);
        _keys.Disable();
        _store.ProtectionKeys = null;

        Changed = true;
        StatusText.Text = $"Encryption turned off — {count} query(s) stored as plain text.";
        Refresh();
    }

    private bool PassphraseAcceptable(string pass)
    {
        // Length is the only thing that meaningfully resists an offline attack on ciphertext.
        if (pass.Length < 12)
        {
            StatusText.Text = "Use at least 12 characters — a memorable phrase works well.";
            return false;
        }
        return true;
    }

    private MessageBoxResult Confirm(string message) =>
        MessageBox.Show(this, message, "Query encryption", MessageBoxButton.YesNo, MessageBoxImage.Warning);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = Changed;
}
