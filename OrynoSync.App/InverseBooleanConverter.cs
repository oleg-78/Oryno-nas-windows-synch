using System.Globalization;
using System.Windows.Data;

namespace OrynoSync.App;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : false;
}
