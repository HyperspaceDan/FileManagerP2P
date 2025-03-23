using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;

namespace FileManagerP2P.Converters
{
    public class DateTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                // Default formatting
                string format = "g"; // Short date and short time

                if (parameter is string customFormat)
                {
                    format = customFormat;
                }

                // If today, just show time
                if (dateTime.Date == DateTime.Today)
                {
                    return $"Today, {dateTime.ToString("t", culture)}";
                }

                // If yesterday
                if (dateTime.Date == DateTime.Today.AddDays(-1))
                {
                    return $"Yesterday, {dateTime.ToString("t", culture)}";
                }

                // If within last week
                if (dateTime > DateTime.Today.AddDays(-7))
                {
                    return dateTime.ToString("dddd, t", culture); // Day name and time
                }

                // Otherwise standard format
                return dateTime.ToString(format, culture);
            }

            return string.Empty;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
