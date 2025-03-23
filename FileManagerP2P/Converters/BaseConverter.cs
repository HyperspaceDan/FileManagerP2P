using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace FileManagerP2P.Converters
{
    /// <summary>
    /// Dummy base converter class to ensure proper imports
    /// </summary>
    public abstract class BaseConverter : IValueConverter
    {
        public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);
        public abstract object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture);
    }
}
