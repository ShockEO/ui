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