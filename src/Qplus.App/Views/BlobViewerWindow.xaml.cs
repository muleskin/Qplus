using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Qplus.Core.Data;

namespace Qplus.App.Views;

/// <summary>
/// A read-only viewer for a single binary cell value: a hex/ASCII dump plus, for recognised
/// image formats, a rendered preview, and a Save-as button to write the raw bytes to disk.
/// Opened by double-clicking a binary cell in a results or data grid.
/// </summary>
public partial class BlobViewerWindow : Window
{
    // Bytes shown in the hex pane are capped so a multi-megabyte blob can't freeze the UI.
    // The full value is always available through Save as…, and the cap is stated in the header.
    private const int HexByteCap = 256 * 1024;

    private readonly byte[] _bytes;
    private readonly BinarySniff.Kind _kind;

    public BlobViewerWindow(byte[] bytes, string columnName)
    {
        InitializeComponent();

        _bytes = bytes;
        _kind = BinarySniff.Identify(bytes);

        Title = $"Binary value — {columnName}";

        var truncated = bytes.Length > HexByteCap;
        HeaderText.Text =
            $"{columnName}  ·  {_kind.Label}  ·  {CellFormat.HumanSize(bytes.Length)} ({bytes.Length:N0} bytes)"
            + (truncated ? $"\nShowing the first {CellFormat.HumanSize(HexByteCap)} below — use Save as… for the whole value." : "");

        HexBox.Text = HexDump(bytes, HexByteCap);

        TryShowImage();
    }

    /// <summary>Decodes and displays the value as an image, or removes the Image tab if it can't.</summary>
    private void TryShowImage()
    {
        if (_kind.IsImage && _bytes.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(_bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now so the stream can close
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                ImageView.Source = bmp;
                Tabs.SelectedItem = ImageTab;                 // lead with the picture when we have one
                return;
            }
            catch
            {
                // Claimed to be an image by its header but wouldn't decode — fall through and
                // drop the tab rather than showing an empty pane.
            }
        }
        Tabs.Items.Remove(ImageTab);
    }

    /// <summary>Classic offset / hex / ASCII dump, 16 bytes per row, up to <paramref name="cap"/> bytes.</summary>
    private static string HexDump(byte[] b, int cap)
    {
        var n = Math.Min(b.Length, cap);
        var sb = new StringBuilder(n / 16 * 78 + 16);
        for (var off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            var rowLen = Math.Min(16, n - off);
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < rowLen ? b[off + i].ToString("X2") + " " : "   ");
                if (i == 7) sb.Append(' ');                  // gap between the two 8-byte halves
            }
            sb.Append(' ');
            for (var i = 0; i < rowLen; i++)
            {
                var c = b[off + i];
                sb.Append(c is >= 32 and < 127 ? (char)c : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save binary value",
            FileName = $"blob.{_kind.Extension}",
            Filter = $"{_kind.Label} (*.{_kind.Extension})|*.{_kind.Extension}|All files (*.*)|*.*",
            DefaultExt = _kind.Extension,
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllBytes(dlg.FileName, _bytes);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the value: " + ex.Message,
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
