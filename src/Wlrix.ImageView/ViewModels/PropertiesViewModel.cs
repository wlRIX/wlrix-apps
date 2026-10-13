// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Reactive;
using ReactiveUI;
using Wlrix.ImageView.Localization;
using Wlrix.ImageView.Models;

namespace Wlrix.ImageView.ViewModels;

/// <summary>
/// The Properties dialog: the Enhance and Geom tabs, as a view onto the main window's live
/// <see cref="MainWindowViewModel.Transform"/> and <see cref="MainWindowViewModel.Enhancement"/>.
/// </summary>
/// <remarks>
/// Holds no image state of its own, so the dialog, the view's gestures and Undo can never
/// disagree. Each setter changes the live state and asks the owner to commit, which it does
/// straight away for a typed value and on release for a drag (see
/// <see cref="MainWindowViewModel.BeginGesture"/>).
/// </remarks>
public sealed class PropertiesViewModel : ViewModelBase
{
    /// <summary>Thumbwheel range for zoom, in powers of two: 1/32 to 64, as the view allows.</summary>
    public const double MinZoomLog = -5, MaxZoomLog = 6;

    /// <summary>Changes smaller than this are a field writing back what it was just given.</summary>
    private const double Tolerance = 1e-9;

    private static readonly string[] EnhanceProperties =
        [nameof(Sharpness), nameof(Brightness), nameof(Haze), nameof(Contrast), nameof(Saturation)];

    private static readonly string[] GeometryProperties =
    [
        nameof(Rotation), nameof(FlipHorizontal), nameof(FlipVertical), nameof(ZoomX), nameof(ZoomY),
        nameof(ZoomXLog), nameof(ZoomYLog), nameof(ImageWidth), nameof(ImageHeight),
    ];

    private readonly MainWindowViewModel _owner;
    private bool _isIndependent;
    private string _hint = string.Empty;

    public PropertiesViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _owner.PropertyChanged += OnOwnerChanged;
        ResetRotation = ReactiveCommand.Create(() => { Rotation = 0; });
    }

    /// <summary>The home button over the dial: straight up again.</summary>
    public ReactiveCommand<Unit, Unit> ResetRotation { get; }

    private Enhancement Enhance => _owner.Enhancement;
    private ViewTransform View => _owner.Transform;

    /// <summary>The open image's name, which heads the dialog.</summary>
    public string FileName => _owner.Document?.FileName ?? Strings.NoValue;

    public bool HasImage => _owner.HasImage;

    /// <summary>What the control under the pointer does, for the dialog's footer.</summary>
    public string Hint
    {
        get => _hint;
        set => this.RaiseAndSetIfChanged(ref _hint, value);
    }

    // ---- Enhance ----

    public double Sharpness
    {
        get => Enhance.Sharpness;
        set => SetEnhance(Enhance with { Sharpness = value });
    }

    public double Brightness
    {
        get => Enhance.Brightness;
        set => SetEnhance(Enhance with { Brightness = value });
    }

    public double Haze
    {
        get => Enhance.Haze;
        set => SetEnhance(Enhance with { Haze = value });
    }

    public double Contrast
    {
        get => Enhance.Contrast;
        set => SetEnhance(Enhance with { Contrast = value });
    }

    public double Saturation
    {
        get => Enhance.Saturation;
        set => SetEnhance(Enhance with { Saturation = value });
    }

    // ---- Geom ----

    /// <summary>Clockwise degrees, 0 to 360. A value outside that wraps, so the spin buttons go round.</summary>
    public double Rotation
    {
        get => View.Rotation;
        set => SetView(View.WithRotation(value));
    }

    public bool FlipHorizontal
    {
        get => View.FlipHorizontal;
        set => SetView(View.WithFlips(value, View.FlipVertical));
    }

    public bool FlipVertical
    {
        get => View.FlipVertical;
        set => SetView(View.WithFlips(View.FlipHorizontal, value));
    }

    /// <summary>
    /// Whether the two zooms are set separately. Off, Horizontal drives both and Vertical
    /// follows it; turning it off brings Vertical back into line.
    /// </summary>
    /// <remarks>
    /// A setting of the dialog, not of the image, so it is not part of an undo step. It starts
    /// on when the image is already stretched, so the dialog never shows a link the zoom does not
    /// keep.
    /// </remarks>
    public bool IsIndependent
    {
        get => _isIndependent || !View.IsUniformZoom;
        set
        {
            this.RaiseAndSetIfChanged(ref _isIndependent, value);
            if (!value && !View.IsUniformZoom)
                SetView(View.WithZoom(View.ZoomX));
        }
    }

    public double ZoomX
    {
        get => View.ZoomX;
        set => SetView(IsIndependent ? View.WithZoom(value, View.ZoomY) : View.WithZoom(value));
    }

    public double ZoomY
    {
        get => View.ZoomY;
        set => SetView(IsIndependent ? View.WithZoom(View.ZoomX, value) : View.WithZoom(value));
    }

    /// <summary>
    /// <see cref="ZoomX"/> as a power of two, for the thumbwheel. A linear wheel over 1/32 to 64
    /// would spend almost all its travel above 1 and leave no control below it.
    /// </summary>
    public double ZoomXLog
    {
        get => Math.Log2(ZoomX);
        set => ZoomX = Math.Pow(2, value);
    }

    public double ZoomYLog
    {
        get => Math.Log2(ZoomY);
        set => ZoomY = Math.Pow(2, value);
    }

    /// <summary>The image's width as drawn, in view pixels. Setting it sets the zoom.</summary>
    public double ImageWidth
    {
        get => _owner.Document is { } document ? Math.Round(document.Width * ZoomX) : 0;
        set
        {
            if (_owner.Document is { Width: > 0 } document && value > 0)
                ZoomX = value / document.Width;
        }
    }

    public double ImageHeight
    {
        get => _owner.Document is { } document ? Math.Round(document.Height * ZoomY) : 0;
        set
        {
            if (_owner.Document is { Height: > 0 } document && value > 0)
                ZoomY = value / document.Height;
        }
    }

    private void SetEnhance(Enhancement next)
    {
        if (!_owner.HasImage)
            return;
        next = next.Clamped();
        if (Near(next.Sharpness, Enhance.Sharpness) && Near(next.Brightness, Enhance.Brightness)
            && Near(next.Haze, Enhance.Haze) && Near(next.Contrast, Enhance.Contrast)
            && Near(next.Saturation, Enhance.Saturation))
        {
            return;
        }

        _owner.Enhancement = next;
        _owner.CommitUnlessGesture();
    }

    private void SetView(ViewTransform next)
    {
        if (!_owner.HasImage)
            return;
        if (Near(next.Rotation, View.Rotation) && Near(next.ZoomX, View.ZoomX) && Near(next.ZoomY, View.ZoomY)
            && next.FlipHorizontal == View.FlipHorizontal && next.FlipVertical == View.FlipVertical)
        {
            return;
        }

        _owner.Transform = next;
        _owner.CommitUnlessGesture();
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < Tolerance;

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.Enhancement):
                foreach (var property in EnhanceProperties)
                    this.RaisePropertyChanged(property);
                break;
            case nameof(MainWindowViewModel.Transform):
                foreach (var property in GeometryProperties)
                    this.RaisePropertyChanged(property);
                this.RaisePropertyChanged(nameof(IsIndependent));
                break;
            case nameof(MainWindowViewModel.Document):
                this.RaisePropertyChanged(nameof(FileName));
                this.RaisePropertyChanged(nameof(HasImage));
                this.RaisePropertyChanged(nameof(ImageWidth));
                this.RaisePropertyChanged(nameof(ImageHeight));
                break;
        }
    }
}
