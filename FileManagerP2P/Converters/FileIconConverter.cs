using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;

namespace FileManagerP2P.Converters
{
    public class FileIconConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string filePath)
                return "document";

            if (string.IsNullOrEmpty(filePath))
                return "document";

            if (value is FileManager.Core.Models.FileSystemItem item)
            {
                if (item.IsDirectory)
                    return "folder";

                filePath = item.Path;
            }

            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "image",
                ".mp3" or ".wav" or ".ogg" or ".flac" => "music_note",
                ".mp4" or ".avi" or ".mov" or ".mkv" => "movie",
                ".pdf" => "picture_as_pdf",
                ".doc" or ".docx" => "description",
                ".xls" or ".xlsx" => "table_chart",
                ".ppt" or ".pptx" => "slideshow",
                ".zip" or ".rar" or ".7z" => "archive",
                ".exe" or ".dll" => "terminal",
                ".txt" => "text_snippet",
                _ => "insert_drive_file"
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

