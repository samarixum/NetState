using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NetState.Shared.Models;

namespace NetState.Client.Converters
{
    public class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CheckStatus status)
            {
                return status switch
                {
                    CheckStatus.Healthy => Brushes.ForestGreen,
                    CheckStatus.Degraded => Brushes.Orange,
                    CheckStatus.Down => Brushes.Crimson,
                    _ => Brushes.Gray
                };
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
