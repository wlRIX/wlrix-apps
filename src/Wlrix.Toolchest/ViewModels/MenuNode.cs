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
    /// <summary>
    /// The header Avalonia reads as "this is a rule, not an item": a <c>MenuItem</c> headed
    /// <c>-</c> takes the <c>:separator</c> pseudo-class and stops being focusable, and
    /// wlrix-avalonia's menu theme templates that into the IRIX etched line.
    /// </summary>
    private const string SeparatorHeader = "-";

    public string Header { get; } = header;
    public IReadOnlyList<MenuNode>? Children { get; } = children;
    public ICommand? Command { get; } = command;
    public bool IsEnabled { get; } = isEnabled;

    /// <summary>A rule between two groups of items.</summary>
    public static MenuNode Separator() => new(SeparatorHeader);

    /// <summary>Whether this node is a <see cref="Separator"/> rather than an item.</summary>
    public bool IsSeparator => Header == SeparatorHeader;
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
