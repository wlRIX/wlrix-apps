// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using ReactiveUI;
using Wlrix.ImageView.Localization;

namespace Wlrix.ImageView.ViewModels;

/// <summary>
/// The Detail dialog: an enlarged view of the pixels under the pointer, what the center one
/// holds, and where the main window's view sits over the image.
/// </summary>
/// <remarks>
/// The main view model fills this in as the pointer moves and the view changes. It holds text
/// for the labels and the point the close-up is centered on, so the dialog has no logic of its
/// own.
/// </remarks>
public sealed class DetailViewModel : ViewModelBase
{
    private Point? _center;
    private int _magnificationIndex = 1;
    private string _positionText = Strings.NoValue;
    private string _pixelText = Strings.NoValue;
    private string _viewOriginText = Strings.NoValue;
    private string _viewSizeText = Strings.NoValue;

    /// <summary>The magnifications the dialog offers, as IRIX imgview's did.</summary>
    public static IReadOnlyList<int> Magnifications { get; } = [1, 2, 4, 6, 8, 10];

    /// <summary><see cref="Magnifications"/> as the combo box shows them.</summary>
    public IReadOnlyList<string> MagnificationNames { get; } =
        [.. Magnifications.Select(Strings.Magnification)];

    /// <summary>The selected entry of <see cref="Magnifications"/>. 2× to start, as on IRIX.</summary>
    public int MagnificationIndex
    {
        get => _magnificationIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _magnificationIndex, Math.Clamp(value, 0, Magnifications.Count - 1));
            this.RaisePropertyChanged(nameof(Magnification));
        }
    }

    public double Magnification => Magnifications[MagnificationIndex];

    /// <summary>
    /// The image point the close-up is centered on, or <c>null</c> before the pointer has been
    /// over an image.
    /// </summary>
    /// <remarks>
    /// Kept when the pointer leaves the image, so the close-up does not go blank on the way to
    /// the dialog.
    /// </remarks>
    public Point? Center
    {
        get => _center;
        private set => this.RaiseAndSetIfChanged(ref _center, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => this.RaiseAndSetIfChanged(ref _positionText, value);
    }

    public string PixelText
    {
        get => _pixelText;
        private set => this.RaiseAndSetIfChanged(ref _pixelText, value);
    }

    public string ViewOriginText
    {
        get => _viewOriginText;
        private set => this.RaiseAndSetIfChanged(ref _viewOriginText, value);
    }

    public string ViewSizeText
    {
        get => _viewSizeText;
        private set => this.RaiseAndSetIfChanged(ref _viewSizeText, value);
    }

    /// <summary>Points the close-up at <paramref name="center"/>, with the value of the pixel there.</summary>
    /// <param name="pixel">The pixel's text, or <c>null</c> when the point is off the image.</param>
    internal void SetPointer(Point center, string? pixel)
    {
        Center = center;
        PositionText = Strings.Coordinates(center.X, center.Y);
        PixelText = pixel ?? Strings.NoValue;
    }

    internal void SetView(Point origin, Size size)
    {
        ViewOriginText = Strings.Coordinates(origin.X, origin.Y);
        ViewSizeText = Strings.ViewSize(size.Width, size.Height);
    }

    /// <summary>Forgets everything, for when no image is open.</summary>
    internal void Clear()
    {
        Center = null;
        PositionText = PixelText = ViewOriginText = ViewSizeText = Strings.NoValue;
    }
}
