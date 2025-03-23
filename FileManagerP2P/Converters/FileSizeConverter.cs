using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;

namespace FileManagerP2P.Converters
{
    public class FileSizeConverter : IValueConverter
    {
        private static readonly string[] _sizes = ["B", "KB", "MB", "GB", "TB"];

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is long size)
            {
                int order = 0;
                double len = size;

                while (len >= 1024 && order < _sizes.Length - 1)
                {
                    order++;
                    len /= 1024;
                }

                return $"{len:0.##} {_sizes[order]}";
            }

            return "0 B";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
