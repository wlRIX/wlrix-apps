// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Wlrix.Writer.Views;

/// <summary>AvaloniaEdit's editor, with a text view that cannot scroll past its own end.</summary>
/// <remarks>
/// Styled as a plain <see cref="TextEditor"/>, so the themes and the Writer's own styles apply to it
/// unchanged.
/// </remarks>
public sealed class WriterTextEditor : TextEditor
{
    public WriterTextEditor() : base(new WriterTextArea())
    {
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    private sealed class WriterTextArea : TextArea
    {
        public WriterTextArea() : base(new ClampedTextView())
        {
        }

        protected override Type StyleKeyOverride => typeof(TextArea);
    }

    /// <summary>A <see cref="TextView"/> whose <see cref="MakeVisible"/> stays inside the extent.</summary>
    /// <remarks>
    /// <para>
    /// While a selection is dragged, AvaloniaEdit keeps the caret in view with a 5 pixel border. At
    /// the end of the longest line the extent has only 3 pixels to spare, and at the last line,
    /// with no scrolling below the document, it has none. So the view scrolls a little past its
    /// end, layout clamps the offset back, and the clamp is itself a scroll, which the drag
    /// answers by scrolling past the end again. Layout gives up after ten rounds a frame. But
    /// TextMate queues a dispatcher job on every scroll, and each job sets the rounds going
    /// again, so the queue grows faster than it drains. The window stops responding and memory
    /// climbs until the process is killed.
    /// </para>
    /// <para>
    /// Clamped here, the scroll the drag asks for is one layout already accepts, and the loop never
    /// starts.
    /// </para>
    /// </remarks>
    private sealed class ClampedTextView : TextView
    {
        protected override Type StyleKeyOverride => typeof(TextView);

        public override void MakeVisible(Rect rectangle)
        {
            // No extent yet means no layout yet, and nothing to clamp against.
            var extent = ((IScrollable)this).Extent;
            var right = extent.Width > 0 ? Math.Min(rectangle.Right, extent.Width) : rectangle.Right;
            var bottom = extent.Height > 0 ? Math.Min(rectangle.Bottom, extent.Height) : rectangle.Bottom;
            var x = Math.Min(rectangle.X, right);
            var y = Math.Min(rectangle.Y, bottom);
            base.MakeVisible(new Rect(x, y, right - x, bottom - y));
        }
    }
}
