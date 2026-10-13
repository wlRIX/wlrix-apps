// SPDX-License-Identifier: GPL-3.0-or-later

using ReactiveUI;

namespace Wlrix.Writer.ViewModels;

/// <summary>The preferences that apply to every editor in the window at once.</summary>
/// <remarks>
/// One instance, shared by every tab, so an editor binds to it directly and follows a change
/// without the window having to find and update each one.
/// </remarks>
public sealed class EditorPreferences : ViewModelBase
{
    private bool _showLineNumbers;

    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set => this.RaiseAndSetIfChanged(ref _showLineNumbers, value);
    }
}
