using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SymlinkManager.Core.Models;

namespace SymlinkManager.App.Converters;

public class LinkStatusStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LinkStatus status || parameter is not string param)
            return Visibility.Collapsed;

        return (status, param) switch
        {
            (LinkStatus.Valid, "valid") => Visibility.Visible,
            (LinkStatus.Broken, "broken") => Visibility.Visible,
            _ => Visibility.Collapsed
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
