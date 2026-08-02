using System.Windows.Input;

namespace Wlrix.Toolchest.ViewModels;

/// <summary>
/// A node in the Toolchest menu tree. A node with <see cref="Children"/> is a submenu; a node
/// with a <see cref="Command"/> is an actionable leaf (e.g. launch an app).
/// </summary>
public sealed class MenuNode(
    string header,
    IReadOnlyList<MenuNode>? children = null,
    ICommand? command = null,
    bool isEnabled = true)
{
    public string Header { get; } = header;
    public IReadOnlyList<MenuNode>? Children { get; } = children;
    public ICommand? Command { get; } = command;
    public bool IsEnabled { get; } = isEnabled;
}

/// <summary>A minimal always-executable <see cref="ICommand"/> for menu leaf actions.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
