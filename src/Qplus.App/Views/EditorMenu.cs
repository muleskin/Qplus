using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;

namespace Qplus.App.Views;

/// <summary>
/// The right-click menu for a SQL editor: Cut, Copy, Paste and Select All. AvalonEdit already
/// binds the keyboard shortcuts but ships with no context menu at all, so right-clicking a
/// selection did nothing.
/// </summary>
internal static class EditorMenu
{
    /// <summary>Gives the editor its context menu. Call once per editor.</summary>
    public static void Attach(TextEditor editor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(NewItem("Cu_t", ApplicationCommands.Cut, editor));
        menu.Items.Add(NewItem("_Copy", ApplicationCommands.Copy, editor));
        menu.Items.Add(NewItem("_Paste", ApplicationCommands.Paste, editor));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("Select _All", ApplicationCommands.SelectAll, editor));

        // Enablement comes from the editor's own command handlers (Paste greys out with nothing
        // on the clipboard); ask for it fresh as the menu opens rather than whenever WPF next
        // gets round to it.
        menu.Opened += (_, _) => CommandManager.InvalidateRequerySuggested();

        editor.ContextMenu = menu;
    }

    /// <summary>
    /// A menu item bound to one of the editor's built-in commands. The menu lives in its own
    /// popup, outside the editor's focus scope, so the command is aimed at the text area
    /// explicitly rather than at whatever holds keyboard focus.
    /// </summary>
    private static MenuItem NewItem(string header, RoutedUICommand command, TextEditor editor) => new()
    {
        Header = header,
        Command = command,
        CommandTarget = editor.TextArea,
    };
}
