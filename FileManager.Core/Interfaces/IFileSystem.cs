using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FileManager.Core.Models;

namespace FileManager.Core.Interfaces
{
    public interface IFileSystem : IQuotaManager
    {
        Task<IEnumerable<FileSystemItem>> ListContents(string path, CancellationToken cancellationToken = default);
        Task<Stream> OpenFile(string path, CancellationToken cancellationToken = default);
        Task WriteFile(string path, Stream content, int bufferSize = 81920, CancellationToken cancellationToken = default);
        Task CreateDirectory(string path, CancellationToken cancellationToken = default);
        Task DeleteItem(string path, CancellationToken cancellationToken = default);
        Task CopyItem(string sourcePath, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
        Task RenameItem(string oldPath, string newPath, CancellationToken cancellationToken = default);
        Task<IEnumerable<FileSystemItem>> ListByType(string path, string extension);
        Dictionary<string, string> GetFileProperties(string path);

        event EventHandler<FileSystemChangeEventArgs>? FileSystemChanged;

    }

}
