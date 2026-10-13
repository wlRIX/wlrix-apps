// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using AvaloniaEdit.Document;
using ReactiveUI;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;
using Wlrix.Writer.Services;

namespace Wlrix.Writer.ViewModels;

/// <summary>What has happened to a document's file that the user has not been told about yet.</summary>
public enum DiskNotice
{
    None,
    Changed,
    Deleted,
}

/// <summary>
/// One text buffer, and the file behind it if it has one.
/// </summary>
/// <remarks>
/// A document can be on screen in both halves of a split window at once. Each half has its own
/// <see cref="EditorTabViewModel"/> with its own caret and scroll position, and both edit this
/// one <see cref="TextDocument"/>, so a change made in one shows in the other as it is typed.
/// </remarks>
public sealed class DocumentViewModel : ViewModelBase, IDisposable
{
    private string? _path;
    private int _untitledNumber;
    private string _displayName;
    private bool _isModified;
    private bool _canUndo;
    private bool _canRedo;
    private string? _languageId;
    private bool _highlightSyntax = true;
    private TabSettings _tabs;

    /// <summary>A new, empty, untitled document.</summary>
    public DocumentViewModel(int untitledNumber, TabSettings tabs)
    {
        Text = new TextDocument();
        _untitledNumber = untitledNumber;
        _displayName = UntitledNames.Format(Strings.Untitled, untitledNumber);
        _tabs = tabs;
        Format = TextFormat.Default;
        Attach();
    }

    /// <summary>A document read from <paramref name="path"/>.</summary>
    public DocumentViewModel(string path, LoadedFile file, string? languageId, TabSettings tabs)
    {
        Text = new TextDocument(file.Text);
        _path = path;
        _displayName = System.IO.Path.GetFileName(path);
        _languageId = languageId;
        _tabs = tabs;
        Format = file.Format;
        Stamp = file.Stamp;
        Attach();
    }

    /// <summary>
    /// An empty document for a file that does not exist yet, as <c>wlrix-writer notes.txt</c>
    /// at a shell starts one. Save writes it without asking for a name.
    /// </summary>
    public DocumentViewModel(string path, string? languageId, TabSettings tabs)
    {
        Text = new TextDocument();
        _path = path;
        _displayName = System.IO.Path.GetFileName(path);
        _languageId = languageId;
        _tabs = tabs;
        Format = TextFormat.Default;
        Attach();
    }

    /// <summary>The text, shared by every editor showing this document.</summary>
    public TextDocument Text { get; }

    /// <summary>The file's full path, or null for a document that has never been saved.</summary>
    public string? Path
    {
        get => _path;
        private set => this.RaiseAndSetIfChanged(ref _path, value);
    }

    /// <summary>For an untitled document, which one: 1 is "Untitled", 2 "Untitled 2". Zero once saved.</summary>
    public int UntitledNumber => _untitledNumber;

    /// <summary>The file name, or "Untitled".</summary>
    public string DisplayName
    {
        get => _displayName;
        private set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    /// <summary>Whether the text differs from what was last read from or written to the file.</summary>
    public bool IsModified
    {
        get => _isModified;
        private set => this.RaiseAndSetIfChanged(ref _isModified, value);
    }

    public bool CanUndo
    {
        get => _canUndo;
        private set => this.RaiseAndSetIfChanged(ref _canUndo, value);
    }

    public bool CanRedo
    {
        get => _canRedo;
        private set => this.RaiseAndSetIfChanged(ref _canRedo, value);
    }

    /// <summary>The highlighting language, or null for Plain.</summary>
    public string? LanguageId
    {
        get => _languageId;
        set => this.RaiseAndSetIfChanged(ref _languageId, value);
    }

    /// <summary>
    /// Whether the language was picked from the menu rather than guessed from the file name.
    /// A picked language survives Save As under a new name; a guessed one is guessed again.
    /// </summary>
    public bool LanguageChosen { get; set; }

    /// <summary>Preferences ▸ Highlight Syntax, for this document.</summary>
    public bool HighlightSyntax
    {
        get => _highlightSyntax;
        set => this.RaiseAndSetIfChanged(ref _highlightSyntax, value);
    }

    /// <summary>Preferences ▸ Tabs, for this document.</summary>
    public TabSettings Tabs
    {
        get => _tabs;
        set => this.RaiseAndSetIfChanged(ref _tabs, value.Normalized());
    }

    /// <summary>How the file was written, so a save writes it back the same way.</summary>
    public TextFormat Format { get; private set; }

    /// <summary>The file as it was last read or written, or null for an untitled document.</summary>
    public DiskStamp? Stamp { get; private set; }

    /// <summary>A change on disk waiting for the window to be in front so it can be asked about.</summary>
    public DiskNotice PendingNotice { get; set; }

    /// <summary>How many tabs are showing this document: one, or two in a split window.</summary>
    public int Views { get; set; }

    /// <summary>
    /// The paths to watch for changes: the file, and through a symbolic link the file it points
    /// at, which is the one that actually changes.
    /// </summary>
    public IReadOnlyList<string> WatchPaths(string path)
    {
        var resolved = DocumentIO.ResolveLinks(path);
        return resolved == path ? [path] : [path, resolved];
    }

    /// <summary>
    /// Whether this is an untitled document nobody has typed in. Opening a file replaces one,
    /// as NEdit reuses an empty window, instead of leaving it behind as a tab.
    /// </summary>
    public bool IsPristine => _path is null && !IsModified && Text.TextLength == 0 && !CanUndo;

    /// <summary>Records a save to <paramref name="path"/>.</summary>
    public void MarkSaved(string path, DiskStamp stamp, TextFormat format)
    {
        Path = path;
        _untitledNumber = 0;
        DisplayName = System.IO.Path.GetFileName(path);
        Format = format;
        Stamp = stamp;
        Text.UndoStack.MarkAsOriginalFile();
        Refresh();
    }

    /// <summary>Replaces the text with the file's current contents.</summary>
    /// <remarks>
    /// Not undoable. Setting <see cref="TextDocument.Text"/> clears the undo history, which is
    /// right: undoing past a reload would step back into a version of the file that no longer
    /// exists anywhere.
    /// </remarks>
    public void Reload(LoadedFile file)
    {
        Text.Text = file.Text;
        Format = file.Format;
        Stamp = file.Stamp;
        Text.UndoStack.ClearAll();
        Text.UndoStack.MarkAsOriginalFile();
        Refresh();
    }

    /// <summary>
    /// Records that the text no longer matches the file: the user kept their version over a
    /// change on disk, or the file was deleted. Saving is then the way to make them agree again.
    /// </summary>
    public void Diverge(DiskStamp? current)
    {
        Stamp = current;
        Text.UndoStack.DiscardOriginalFileMarker();
        Refresh();
    }

    public void Dispose() => Text.UndoStack.PropertyChanged -= OnUndoStackChanged;

    private void Attach()
    {
        Text.UndoStack.PropertyChanged += OnUndoStackChanged;
        Refresh();
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var stack = Text.UndoStack;
        IsModified = !stack.IsOriginalFile;
        CanUndo = stack.CanUndo;
        CanRedo = stack.CanRedo;
    }
}
