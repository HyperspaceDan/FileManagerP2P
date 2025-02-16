using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FileManager.Core.Models;

namespace FileManager.Core.Interfaces
{
    public interface IFileSystem
    {
        Task<IEnumerable<FileSystemItem>> ListContents(string path);
        Task<Stream> OpenFile(string path);
        Task WriteFile(string path, Stream content);
        Task CreateDirectory(string path);
        Task DeleteItem(string path);
        Task CopyItem(string sourcePath, string destinationPath);
        Task RenameItem(string oldPath, string newPath);
    }

}
