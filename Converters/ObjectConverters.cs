using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ShockUI.Converters;

public static class ObjectConverters
{
    public static readonly IValueConverter IsNotNull =
        new FuncValueConverter<object?, bool>(value => value is not null);

    public static readonly IValueConverter IsNull =
        new FuncValueConverter<object?, bool>(value => value is null);


    /// <summary>
    /// Multi-value AND: returns true only when every bound source evaluates to true.
    /// Usage: <MultiBinding Converter="{x:Static conv:ObjectConverters.AllTrue}">
    /// </summary>
    public static readonly IMultiValueConverter AllTrue = new AllTrueConverter();


    /// <summary>
    /// Maps a bool to a column count for a <c>UniformGrid</c>:
    ///   true  → 1   (video visible — single column, sections stack vertically)
    ///   false → 2   (video hidden — two columns, sections sit side-by-side)
    /// Inverted form lets us bind directly to <c>IsVideoFeedVisible</c>.
    /// </summary>
    public static readonly IValueConverter VideoVisibleToColumns =
        new FuncValueConverter<bool, int>(visible => visible ? 1 : 2);

    /// <summary>
    /// Computes a sensible MaxHeight for the right column's "extra" slot
    /// (Decoded Frame + Pan/Tilt Debugger) from the shell's total height, so
    /// that slot scrolls within one scrollbar instead of overflowing when the
    /// window is short. We reserve headroom for the header, the fixed Log
    /// (200) and Trace (170) panels, spacing and margins (~520px), and clamp
    /// to a usable minimum so the area never collapses to nothing.
    /// </summary>
    public static readonly IValueConverter RightExtraMaxHeight =
        new FuncValueConverter<double, double>(total =>
        {
            const double reserved = 540.0;   // header + log + trace + spacing
            double avail = total - reserved;
            if (double.IsNaN(avail) || avail < 160.0) return 160.0;
            return avail;
        });

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            foreach (var v in values)
                if (v is not true) return false;
            return true;
        }
    }

    private sealed class FuncValueConverter<TIn, TOut> : IValueConverter
    {
        private readonly Func<TIn?, TOut> _converter;

        public FuncValueConverter(Func<TIn?, TOut> converter)
        {
            _converter = converter;
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => _converter((TIn?)value);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}