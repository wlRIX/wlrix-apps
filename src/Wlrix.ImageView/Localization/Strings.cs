// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Localization;

namespace Wlrix.ImageView.Localization;

/// <summary>The application's strings.</summary>
/// <remarks>
/// Hand-written rather than generated, matching the other localized apps. A missing key comes
/// back as the key itself, which leaves the blemish visible in the window instead of throwing.
///
/// Menus and labels use <c>{loc:Tr}</c> against <see cref="Catalog"/> directly; the members
/// here are for the strings put together in C#.
/// </remarks>
public static class Strings
{
    public static StringCatalog Catalog { get; } =
        new("Wlrix.ImageView.Localization.Strings", typeof(Strings).Assembly);

    public static string AppName => Catalog.Get("AppName");
    public static string WindowTitle(string name) => Catalog.Format("WindowTitle", name);

    public static string HintHand => Catalog.Get("HintHand");
    public static string HintRotate => Catalog.Get("HintRotate");
    public static string HintZoom => Catalog.Get("HintZoom");
    public static string Loading(string name) => Catalog.Format("StatusLoading", name);
    public static string Saving(string name) => Catalog.Format("StatusSaving", name);
    public static string EmptyNoImage => Catalog.Get("EmptyNoImage");

    public static string OpenTitle => Catalog.Get("OpenTitle");
    public static string SaveAsTitle => Catalog.Get("SaveAsTitle");
    public static string FilterImages => Catalog.Get("FilterImages");
    public static string FilterAllFiles => Catalog.Get("FilterAllFiles");

    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string About(string version) => Catalog.Format("AboutMessage", version);
    public static string ConfirmTitle => Catalog.Get("ConfirmTitle");
    public static string ConfirmDiscard(string name) => Catalog.Format("ConfirmDiscard", name);
    public static string OpenFailed(string name, string reason) => Catalog.Format("ErrorOpenFailed", name, reason);
    public static string SaveFailed(string name, string reason) => Catalog.Format("ErrorSaveFailed", name, reason);
    public static string CannotWrite(string format) => Catalog.Format("ErrorCannotWrite", format);
    public static string UnknownType(string name) => Catalog.Format("ErrorUnknownType", name);

    public static string Rle(string format) => Catalog.Format("FormatRle", format);
    public static string Verbatim(string format) => Catalog.Format("FormatVerbatim", format);
    public static string Dimensions(int width, int height) => Catalog.Format("DimensionsValue", width, height);
    public static string Frames(int count) => Catalog.Format("FramesValue", count);
    public static string Pan(double x, double y) => Catalog.Format("PanValue", x, y);
    public static string NoValue => Catalog.Get("NoValue");

    /// <summary>"853, 801.982": an image point, to three places where it has them.</summary>
    public static string Coordinates(double x, double y) => Catalog.Format("CoordinatesValue", x, y);

    /// <summary>"694 x 555": the visible part of the image, in image pixels.</summary>
    public static string ViewSize(double width, double height) => Catalog.Format("ViewSizeValue", width, height);

    /// <summary>"2X", a close-up's magnification.</summary>
    public static string Magnification(int factor) => Catalog.Format("MagnificationValue", factor);

    public static string Channels(int channels) => channels switch
    {
        1 => Catalog.Get("ChannelsGray"),
        2 => Catalog.Get("ChannelsGrayAlpha"),
        3 => Catalog.Get("ChannelsRgb"),
        _ => Catalog.Get("ChannelsRgba"),
    };

    /// <summary>"2.4 MB (2,516,582 bytes)": a size to read at a glance, and the exact count.</summary>
    /// <remarks>Binary multiples under the short names, as IRIX and most file managers show them.</remarks>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
            return Catalog.Format("FileSizeBytes", bytes);

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        return Catalog.Format("FileSizeScaled", $"{value:0.#} {units[unit]}", bytes);
    }
}
