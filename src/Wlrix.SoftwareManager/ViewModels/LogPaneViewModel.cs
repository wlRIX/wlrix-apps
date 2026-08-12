using System.Text;
using ReactiveUI;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The Log pane: everything the package manager said, in order. Fed a line at a time from the
/// transaction stream.
/// </summary>
public sealed class LogPaneViewModel : ViewModelBase
{
    // A package manager upgrading a whole system produces thousands of lines, and rebuilding a
    // string per line would be quadratic. The builder accumulates; the bound property is only
    // materialized when the pane actually asks for it.
    private readonly StringBuilder _builder = new();

    /// <summary>The accumulated output.</summary>
    public string Text => _builder.ToString();

    /// <summary>Appends one line and tells the pane to re-read.</summary>
    public void Append(string line)
    {
        _builder.AppendLine(line);
        this.RaisePropertyChanged(nameof(Text));
    }

    /// <summary>Empties the pane, at the start of a transaction.</summary>
    public void Clear()
    {
        _builder.Clear();
        this.RaisePropertyChanged(nameof(Text));
    }
}
