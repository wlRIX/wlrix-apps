// SPDX-License-Identifier: GPL-3.0-or-later

using ReactiveUI;
using Wlrix.ImageView.Localization;
using Wlrix.ImageView.Models;

namespace Wlrix.ImageView.ViewModels;

/// <summary>The Histogram dialog: the counts for the open image and which channel is shown.</summary>
public sealed class HistogramViewModel : ViewModelBase
{
    private Histogram? _histogram;
    private int _channelIndex;

    /// <summary>The channel choices, in <see cref="HistogramChannel"/> order.</summary>
    public IReadOnlyList<string> ChannelNames { get; } =
    [
        Strings.Catalog.Get("ChannelRgb"),
        Strings.Catalog.Get("ChannelRed"),
        Strings.Catalog.Get("ChannelGreen"),
        Strings.Catalog.Get("ChannelBlue"),
        Strings.Catalog.Get("ChannelLuminance"),
    ];

    public Histogram? Histogram
    {
        get => _histogram;
        set
        {
            this.RaiseAndSetIfChanged(ref _histogram, value);
            RaiseStats();
        }
    }

    /// <summary>The selected entry of <see cref="ChannelNames"/>.</summary>
    public int ChannelIndex
    {
        get => _channelIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _channelIndex, Math.Clamp(value, 0, ChannelNames.Count - 1));
            this.RaisePropertyChanged(nameof(Channel));
            RaiseStats();
        }
    }

    public HistogramChannel Channel => (HistogramChannel)ChannelIndex;

    private HistogramStats? Stats => Histogram is { PixelCount: > 0 } histogram ? histogram.Stats(Channel) : null;

    public string MinText => Stats is { } stats ? stats.Min.ToString() : Strings.NoValue;
    public string MaxText => Stats is { } stats ? stats.Max.ToString() : Strings.NoValue;
    public string MeanText => Stats is { } stats ? stats.Mean.ToString("0.0") : Strings.NoValue;
    public string MedianText => Stats is { } stats ? stats.Median.ToString() : Strings.NoValue;
    public string PixelsText => Histogram is { } histogram ? histogram.PixelCount.ToString("N0") : Strings.NoValue;

    private void RaiseStats()
    {
        this.RaisePropertyChanged(nameof(MinText));
        this.RaisePropertyChanged(nameof(MaxText));
        this.RaisePropertyChanged(nameof(MeanText));
        this.RaisePropertyChanged(nameof(MedianText));
        this.RaisePropertyChanged(nameof(PixelsText));
    }
}
