using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using MAUIStorage = Microsoft.Maui.Storage;
using FileManager.Core.Interfaces;
using FileManager.Core.Models;


namespace FileManagerP2P.Platforms.Windows
{
    public class WindowsFileSystem : FileManager.Core.Interfaces.IFileSystem
    {
        public async Task<IEnumerable<FileSystemItem>> ListContents(string path)
        {
            return await Task.Run(() => Directory.GetFileSystemEntries(path)
                .Select(entry => new FileSystemItem
                {
                    Name = Path.GetFileName(entry),
                    Path = entry,
                    IsDirectory = Directory.Exists(entry),
                    Size = File.Exists(entry) ? new FileInfo(entry).Length : 0,
                    ModifiedDate = File.GetLastWriteTime(entry)
                }));
        }

        public Task<Stream> OpenFile(string path)
            => Task.FromResult<Stream>(File.OpenRead(path));

        public Task CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }

        public Task DeleteItem(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            else if (File.Exists(path))
                File.Delete(path);
            return Task.CompletedTask;
        }

        // SPOS-required additions
        public Task WriteFile(string path, Stream content)
        {
            using var fileStream = new FileStream(path, FileMode.Create);
            content.CopyTo(fileStream);
            return Task.CompletedTask;
        }

        public Task CopyItem(string sourcePath, string destinationPath)
        {
            if (File.Exists(sourcePath))
                File.Copy(sourcePath, destinationPath);
            else if (Directory.Exists(sourcePath))
                CopyDirectory(sourcePath, destinationPath);
            return Task.CompletedTask;
        }

        public Task RenameItem(string oldPath, string newPath)
        {
            if (File.Exists(oldPath))
                File.Move(oldPath, newPath);
            else if (Directory.Exists(oldPath))
                Directory.Move(oldPath, newPath);
            return Task.CompletedTask;
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            Directory.CreateDirectory(destDir);

            foreach (var file in dir.GetFiles())
                file.CopyTo(Path.Combine(destDir, file.Name));

            foreach (var subDir in dir.GetDirectories())
                CopyDirectory(subDir.FullName, Path.Combine(destDir, subDir.Name));
        }
    }
}

