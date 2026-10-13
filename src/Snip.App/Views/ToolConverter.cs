using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Snip.Core;

namespace Snip.App.Views;

public sealed class ToolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is AnnotationTool tool && tool.ToString() == (string)parameter;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Enum.Parse<AnnotationTool>((string)parameter) : Binding.DoNothing;
}
