namespace Qplus.Core.Data;

/// <summary>Runs an action on dispose — used to unsubscribe provider event handlers.</summary>
public sealed class DisposableAction : IDisposable
{
    private Action? _action;
    public DisposableAction(Action action) => _action = action;

    public void Dispose()
    {
        _action?.Invoke();
        _action = null;
    }
}
