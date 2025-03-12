using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Security;

namespace FileManagerP2P.Services
{
    public static class PathValidator
    {
        public static void ValidateCustomRootPath(string path)
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty", nameof(path));

            // Check for invalid characters
            if (Path.GetInvalidPathChars().Any(path.Contains))
                throw new ArgumentException("Path contains invalid characters", nameof(path));

            // Check for relative paths or path traversal attempts
            if (path.Contains("..") || path.Contains('~'))
                throw new SecurityException("Path contains potentially unsafe segments");

            string fullPath = Path.GetFullPath(path);

            // Check for system directories
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);

            if (fullPath.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(system, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("Cannot use system directories as storage location");
            }

            // Check for write access
            try
            {
                string testFile = Path.Combine(path, $"write_test_{Guid.NewGuid()}.tmp");
                File.WriteAllText(testFile, string.Empty);
                File.Delete(testFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException($"No write permission to path: {path}", ex);
            }
        }
    }
}
