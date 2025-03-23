using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;

namespace FileManagerP2P.Converters
{
    public class PathToFilenameConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
            {
                return Path.GetFileName(path);
            }

            if (value is FileManager.Core.Models.FileSystemItem item)
            {
                return item.Name;
            }

            return string.Empty;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

