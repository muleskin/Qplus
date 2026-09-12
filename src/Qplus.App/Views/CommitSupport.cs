using System.Windows.Controls;

namespace Qplus.App.Views;

/// <summary>
/// A tab whose pending changes the toolbar's Commit and Rollback act on: an open transaction in
/// a query tab, or unsaved edits in a table's Data pane.
/// </summary>
internal interface ICommitTarget
{
    /// <summary>Whether there is anything to commit or roll back.</summary>
    bool CanCommit { get; }

    /// <summary>Raised when <see cref="CanCommit"/> may have changed.</summary>
    event EventHandler? CommitStateChanged;

    Task CommitAsync();

    Task RollbackAsync();

    /// <summary>
    /// Settles anything uncommitted before the tab goes away, asking the user if need be.
    /// False means keep the tab open.
    /// </summary>
    Task<bool> PrepareToCloseAsync();
}

/// <summary>The Commit / Rollback pair a right-click menu offers, and when they apply.</summary>
internal sealed record CommitActions(Func<bool> CanCommit, Action Commit, Action Rollback)
{
    public static CommitActions For(ICommitTarget target) =>
        new(() => target.CanCommit, () => _ = target.CommitAsync(), () => _ = target.RollbackAsync());
}

/// <summary>Adds Commit and Rollback to a right-click menu, enabled only while something is pending.</summary>
internal static class CommitMenuItems
{
    public static void Add(ContextMenu menu, CommitActions actions)
    {
        var commit = new MenuItem { Header = "C_ommit" };
        commit.Click += (_, _) => actions.Commit();
        var rollback = new MenuItem { Header = "_Rollback" };
        rollback.Click += (_, _) => actions.Rollback();

        menu.Items.Add(commit);
        menu.Items.Add(rollback);
        menu.Opened += (_, _) => commit.IsEnabled = rollback.IsEnabled = actions.CanCommit();
    }
}
