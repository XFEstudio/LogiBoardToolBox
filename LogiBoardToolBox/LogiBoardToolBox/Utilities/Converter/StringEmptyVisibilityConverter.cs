using Microsoft.UI.Xaml.Data;
using XFEExtension.NetCore.StringExtension;

namespace LogiBoardToolBox.Utilities.Converter
{
    public partial class StringEmptyVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => value is not string stringValue || stringValue.NullOrEmpty ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language) => value;
    }
}