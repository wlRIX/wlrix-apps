// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ImageView.Models;

/// <summary>Everything one undo step covers: how the image is viewed and how it is enhanced.</summary>
public readonly record struct ImageState(ViewTransform View, Enhancement Enhance)
{
    public static ImageState Identity { get; } = new(ViewTransform.Identity, Enhancement.Identity);

    /// <summary>Whether a save would change the file: a turn, a reflection or any enhancement.</summary>
    public bool IsModified => View.IsReoriented || !Enhance.IsIdentity;
}
