using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using MAUIStorage = Microsoft.Maui.Storage;
using FileManager.Core.Interfaces;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;


namespace FileManagerP2P.Platforms.Windows
{
    public partial class WindowsFileSystem : FileManager.Core.Interfaces.IFileSystem, IDisposable
    {
        private readonly ILogger <WindowsFileSystem> _logger;
        private readonly string _rootPath;
        public WindowsFileSystem(ILogger<WindowsFileSystem> logger, string rootPath)
        {
            _logger = logger;
            _rootPath = Path.GetFullPath(rootPath);
        }
        private void ValidatePathWithinRoot(string path)
        {
            ValidatePath(path);

            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Attempted access outside root path: {Path}", path);
                throw new UnauthorizedAccessException("Access to the path is restricted");
            }
        }

        private async Task<T> WithFileSystemErrorHandling<T>(string path, Func<Task<T>> action)
        {
            try
            {
                return await action();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied to path: {Path}", path);
                throw new UnauthorizedAccessException($"Access denied to path: {path}", ex);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO error accessing path: {Path}", path);
                throw new IOException($"IO error accessing path: {path}", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error accessing path: {Path}", path);
                throw new IOException($"Error accessing path: {path}", ex);
            }
        }

        public async Task<IEnumerable<FileSystemItem>> ListContents(string path, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException($"Directory not found: {path}");

                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                return Directory.GetFileSystemEntries(path, "*", options)
                    .Select(entry =>
                    {
                    var info = new FileInfo(entry);
                    return new FileSystemItem
                    {
                        Name = info.Name,
                        Path = entry,
                        IsDirectory = (info.Attributes & FileAttributes.Directory) != 0,
                        Size = info.Exists && (info.Attributes & FileAttributes.Directory) == 0 ? info.Length : 0,

                        ModifiedDate = info.LastWriteTime
                    };
                })
                .ToList(); // Materialize the list while still in the Task.Run
            }, cancellationToken);
        }

        public async Task<Stream> OpenFile(string path)
        {
            ThrowIfDisposed();
            ValidatePathWithinRoot(path);
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");

            return await WithFileSystemErrorHandling<Stream>(path, async () =>
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > 100 * 1024 * 1024) // Files larger than 100MB
                {
                    // Use FileStream directly for large files
                    return new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                }
                else
                {
                    // Use MemoryStream for smaller files
                    var memoryStream = new MemoryStream((int)fileInfo.Length);
                    using var fileStream = File.OpenRead(path);
                    await fileStream.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;
                    return memoryStream;
                }
            });
        }

        private static async Task RetryOnIO(Func<Task> action, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    await action();
                    return;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    await Task.Delay((i + 1) * 100); // Exponential backoff
                }
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                // Cleanup any cached resources
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WindowsFileSystem));
        }

        


        public async Task WriteFile(string path, Stream content, int bufferSize = 81920, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidatePathWithinRoot(path);
            // Add directory creation if needed
            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                await CreateDirectory(Path.GetDirectoryName(path)!);
                await RetryOnIO(async () =>
                {
                    using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
                    await content.CopyToAsync(fileStream, bufferSize, cancellationToken);
                });
                return Task.CompletedTask;
            });
        }

        public Task CreateDirectory(string path)
        {
            ValidatePath(path);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }


        public async Task DeleteItem(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidatePathWithinRoot(path);

            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new FileNotFoundException($"Path not found: {path}");

                await Task.Run(() =>
                {
                    if (Directory.Exists(path))
                        DeleteDirectoryRecursive(path, cancellationToken);
                    else
                        File.Delete(path);
                }, cancellationToken);
                return Task.CompletedTask;
            });
        }

        private static void DeleteDirectoryRecursive(string path, CancellationToken cancellationToken)
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteDirectoryRecursive(dir, cancellationToken);
            }
            foreach (var file in Directory.GetFiles(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
            }
            Directory.Delete(path);
        }

        public async Task CopyItem(string sourcePath, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            ValidatePathWithinRoot(sourcePath);
            ValidatePathWithinRoot(destinationPath);

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                throw new FileNotFoundException($"Source path not found: {sourcePath}");

            await Task.Run(async () =>  // Changed to async lambda
            {
                if (File.Exists(sourcePath))
                    await CopyFileWithProgress(sourcePath, destinationPath, progress, cancellationToken);
                else
                    await CopyDirectoryWithProgress(sourcePath, destinationPath, progress, cancellationToken);
            }, cancellationToken);
        }

        // CopyFileWithProgress and WriteFile need the same WinRT compatibility fixes
        private static async Task CopyFileWithProgress(string sourcePath, string destPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            const int bufferSize = 1024 * 1024; // 1MB buffer

            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
            using var destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);

            var totalBytes = source.Length;
            var bytesRead = 0L;
            var buffer = new byte[bufferSize];

            while (bytesRead < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentRead = await source.ReadAsync(buffer, cancellationToken);
                if (currentRead == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, currentRead), cancellationToken);
                bytesRead += currentRead;
                progress?.Report((double)bytesRead / totalBytes);
            }
        }

        private static async Task CopyDirectoryWithProgress(string sourceDir, string destDir, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destDir);
            var files = Directory.GetFiles(sourceDir);
            var directories = Directory.GetDirectories(sourceDir);
            var totalItems = files.Length + directories.Length;
            var currentItem = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                await CopyFileWithProgress(file, destFile, progress, cancellationToken);
                currentItem++;
                progress?.Report((double)currentItem / totalItems);
            }

            foreach (var dir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                await CopyDirectoryWithProgress(dir, destSubDir, progress, cancellationToken);
                currentItem++;
                progress?.Report((double)currentItem / totalItems);
            }
        }

        public async Task RenameItem(string oldPath, string newPath)
        {
            ThrowIfDisposed();
            ValidatePath(oldPath);
            ValidatePath(newPath);

            await WithFileSystemErrorHandling<Task>(oldPath, async () =>
            {
                if (!File.Exists(oldPath) && !Directory.Exists(oldPath))
                    throw new FileNotFoundException($"Path not found: {oldPath}");

                await RetryOnIO(() =>
                {
                    if (File.Exists(oldPath))
                        File.Move(oldPath, newPath);
                    else
                        Directory.Move(oldPath, newPath);
                    return Task.CompletedTask;
                });
                return Task.CompletedTask;
            });
        }

        public async Task<IEnumerable<FileSystemItem>> ListByType(string path, string extension)
        {
            var contents = await ListContents(path);
            return contents.Where(f => !f.IsDirectory && f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        public Dictionary<string, string> GetFileProperties(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");

            var info = new FileInfo(path);
            return new Dictionary<string, string>
            {
                ["Size"] = info.Length.ToString(),
                ["Created"] = info.CreationTime.ToString(),
                ["Modified"] = info.LastWriteTime.ToString(),
                ["Attributes"] = info.Attributes.ToString(),
                ["IsReadOnly"] = info.IsReadOnly.ToString()
            };
        }
        private static void ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty", nameof(path));

            // Check for invalid characters
            if (Path.GetInvalidPathChars().Any(path.Contains))
                throw new ArgumentException("Path contains invalid characters", nameof(path));

            // Normalize the path to prevent path traversal attacks
            try
            {
                // Get the full path and normalize it
                string fullPath = Path.GetFullPath(path);

                // Optional: Add a root path check if you want to restrict access to specific directories
                // string rootPath = Path.GetFullPath(yourRootPath);
                // if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                //     throw new UnauthorizedAccessException("Access to the path is restricted");

                // Verify the path after normalization doesn't contain relative segments
                if (fullPath.Contains("..", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains("./", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains(".\\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Path contains invalid relative segments", nameof(path));
                }
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                throw new ArgumentException("Invalid path", nameof(path), ex);
            }
        }
    }
}

