using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// The theme's combo box list, which Files' Connect dialog uses as every other app does: a click
/// outside it closes it.
/// </summary>
/// <remarks>
/// The regression: the list's popup had no light dismiss, so it stayed open. On Wayland it holds
/// the popup grab, and opening a second combo box while it was up dropped the app's connection.
/// </remarks>
public sealed class ComboBoxDismissTests
{
    private sealed class Harness
    {
        public Harness()
        {
            First = new ComboBox { ItemsSource = new[] { "one", "two", "three" }, SelectedIndex = 0 };
            Second = new ComboBox { ItemsSource = new[] { "four", "five" }, SelectedIndex = 0 };
            Window = new Window
            {
                Width = 400,
                Height = 300,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Children = { First, Second },
                },
            };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public Window Window { get; }
        public ComboBox First { get; }
        public ComboBox Second { get; }

        public void Click(Point point)
        {
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(Control control) =>
            Click(control.TranslatePoint(new Point(10, control.Bounds.Height / 2), Window)!.Value);
    }

    [Fact]
    public Task AClickOutsideTheListClosesIt() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            var harness = new Harness();
            harness.Click(harness.First);
            Assert.True(harness.First.IsDropDownOpen);

            // The window's empty lower right, well away from the list.
            harness.Click(new Point(380, 280));

            Assert.False(harness.First.IsDropDownOpen);
            harness.Window.Close();
        }, CancellationToken.None);

    [Fact]
    public Task AClickOnAnotherComboBoxClosesTheOpenList() =>
        HeadlessFiles.Session.Dispatch(() =>
        {
            var harness = new Harness();
            harness.Click(harness.First);
            Assert.True(harness.First.IsDropDownOpen);

            harness.Click(harness.Second);

            // The click only closes the open list, so no second list asks for a grab while the
            // first holds one. Headless has no grabs, and this passed before the fix as well:
            // the crash itself was only seen on Wayland.
            Assert.False(harness.First.IsDropDownOpen);
            Assert.False(harness.Second.IsDropDownOpen);
            harness.Window.Close();
        }, CancellationToken.None);
}
