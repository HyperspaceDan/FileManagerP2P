using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;

namespace FileManagerP2P.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool visibility = false;

            if (value is bool boolValue)
            {
                visibility = boolValue;
            }

            // Check if we need to invert the value
            if (parameter is string paramString &&
                paramString.Equals("invert", StringComparison.OrdinalIgnoreCase))
            {
                visibility = !visibility;
            }

            return visibility;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool visibility)
            {
                bool result = visibility;

                // Check if we need to invert the value
                if (parameter is string paramString &&
                    paramString.Equals("invert", StringComparison.OrdinalIgnoreCase))
                {
                    result = !result;
                }

                return result;
            }

            return false;
        }
    }
}
