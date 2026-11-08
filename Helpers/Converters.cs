using System;
using System.Globalization;
using System.Windows.Data;

namespace Crest.Helpers
{
    /// <summary>3000 → 3 (used to show the peek duration in seconds).</summary>
    public sealed class MillisecondsToSecondsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is IConvertible c ? c.ToDouble(culture) / 1000.0 : 0.0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
