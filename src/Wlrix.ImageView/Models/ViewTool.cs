// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ImageView.Models;

/// <summary>What a left-button drag over the image does.</summary>
public enum ViewTool
{
    /// <summary>Drags (pans) the image.</summary>
    Hand,

    /// <summary>Rotates the image about the center of the view.</summary>
    Rotate,

    /// <summary>Zooms the image: up or left zooms in, down or right zooms out.</summary>
    Zoom,
}
