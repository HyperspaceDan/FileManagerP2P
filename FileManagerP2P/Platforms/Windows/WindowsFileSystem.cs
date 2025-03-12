using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Maui.Storage;
using FileManager.Core.Interfaces;
using FileManager.Core.Models;
using FileManager.Core.Exceptions;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;


namespace FileManagerP2P.Platforms.Windows
{

    public partial class WindowsFileSystem : FileManager.Core.Interfaces.IFileSystem, IDisposable, IAsyncDisposable
    {
        public static async Task<WindowsFileSystem> CreateWithSecurePathAsync(
            ILogger<WindowsFileSystem> logger,
            IFileSystemPathProvider pathProvider)
        {
            ArgumentNullException.ThrowIfNull(pathProvider);

            var rootPath = await pathProvider.GetRootPathAsync();

            return new WindowsFileSystem(logger, rootPath, new QuotaConfiguration
            {
                MaxSizeBytes = 1024 * 1024 * 1024, // 1GB default
                RootPath = rootPath,
                WarningThreshold = 0.9f,
                EnforceQuota = true
            });
        }

        public static async Task<WindowsFileSystem> CreateWithSecurePathAsync(
            ILogger<WindowsFileSystem> logger,
            IFileSystemPathProvider pathProvider,
            QuotaConfiguration baseConfig)
        {
            ArgumentNullException.ThrowIfNull(pathProvider);
            ArgumentNullException.ThrowIfNull(baseConfig);

            var rootPath = await pathProvider.GetRootPathAsync();

            // Create a new QuotaConfiguration with the secure path
            var quotaConfig = new QuotaConfiguration
            {
                MaxSizeBytes = baseConfig.MaxSizeBytes,
                RootPath = rootPath, // Override with secure path
                WarningThreshold = baseConfig.WarningThreshold,
                EnforceQuota = baseConfig.EnforceQuota
            };

            return new WindowsFileSystem(logger, rootPath, quotaConfig);
        }



        private long? _cachedUsage;
        private DateTime _lastUsageCheck = DateTime.MinValue;
        private static readonly TimeSpan UsageCacheDuration = TimeSpan.FromMinutes(5);

        private readonly QuotaConfiguration _quotaConfig;

        private FileSystemWatcher? _watcher;
        public event EventHandler<FileSystemChangeEventArgs>? FileSystemChanged;

        private readonly ILogger <WindowsFileSystem> _logger;
        private readonly string _rootPath;
        public WindowsFileSystem(ILogger<WindowsFileSystem> logger)
        : this(logger, FileSystem.AppDataDirectory, new QuotaConfiguration
        {
            MaxSizeBytes = 1024 * 1024 * 1024, // 1GB default
            RootPath = FileSystem.AppDataDirectory,
            WarningThreshold = 0.9f,
            EnforceQuota = true
        })
        {
        }
        public WindowsFileSystem(ILogger<WindowsFileSystem> logger, string rootPath, QuotaConfiguration quotaConfig)
        {
            ArgumentNullException.ThrowIfNull(quotaConfig);
            if (quotaConfig.MaxSizeBytes <= 0)
                throw new ArgumentException("MaxSizeBytes must be positive", nameof(quotaConfig));
            if (quotaConfig.WarningThreshold <= 0 || quotaConfig.WarningThreshold > 1)
                throw new ArgumentException("WarningThreshold must be between 0 and 1", nameof(quotaConfig));



                    _logger = logger;
            _rootPath = Path.GetFullPath(rootPath);
            _quotaConfig = quotaConfig;

            InitializeFileWatcher();

            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
            }
        }

        public event EventHandler<QuotaWarningEventArgs>? QuotaWarningRaised;


        public async Task<QuotaInfo> GetQuotaInfo(CancellationToken cancellationToken = default)
        {
            var currentUsage = await GetCurrentUsageInternal(cancellationToken);
            var percentage = (float)currentUsage / _quotaConfig.MaxSizeBytes;

            return new QuotaInfo(
                currentUsage,
                _quotaConfig.MaxSizeBytes,
                percentage,
                currentUsage >= _quotaConfig.MaxSizeBytes
            );
        }

        private readonly ReaderWriterLockSlim _cacheLock = new(LockRecursionPolicy.NoRecursion);
        private void InvalidateUsageCache()
        {
            try
            {
                _cacheLock.EnterWriteLock();
                _cachedUsage = null;
                _lastUsageCheck = DateTime.MinValue;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }
        private async Task<long> GetCurrentUsageInternal(CancellationToken cancellationToken)
        {
            try
            {
                _cacheLock.EnterReadLock();
                if (_cachedUsage.HasValue && DateTime.UtcNow - _lastUsageCheck < UsageCacheDuration)
                        return _cachedUsage.Value;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
            var usage = await Task.Run(() =>
            {
                return Directory.GetFiles(_rootPath, "*", SearchOption.AllDirectories)
                              .Sum(f => new FileInfo(f).Length);
            }, cancellationToken);
            try
            {
                _cacheLock.EnterWriteLock();
                _cachedUsage = usage;
                _lastUsageCheck = DateTime.UtcNow;
                return usage;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private static async Task<long> GetDirectorySize(string path, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length)
            , cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await DisposeAsyncCore();
                Dispose(false);
                GC.SuppressFinalize(this);
            }

        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            // Any async cleanup
            await Task.Run(() =>
            {
                // Release semaphores
                foreach (var semaphore in _fileLocks.Values)
                {
                    semaphore.Dispose();
                }
                _fileLocks.Clear();
            });

        }
        public async Task ValidateQuota(long requiredBytes, CancellationToken cancellationToken = default)
        {
            if (!_quotaConfig.EnforceQuota) return;

            var info = await GetQuotaInfo(cancellationToken);
            var projectedUsage = info.CurrentUsageBytes + requiredBytes;

            if (projectedUsage >= _quotaConfig.MaxSizeBytes)
            {
                throw new QuotaExceededException(requiredBytes,
                    _quotaConfig.MaxSizeBytes - info.CurrentUsageBytes);
            }

            if (projectedUsage >= _quotaConfig.MaxSizeBytes * _quotaConfig.WarningThreshold)
            {
                OnQuotaWarningRaised(new QuotaWarningEventArgs
                {
                    CurrentUsage = info.CurrentUsageBytes,
                    QuotaLimit = _quotaConfig.MaxSizeBytes,
                    UsagePercentage = info.UsagePercentage
                });
            }
        }

        private async Task ValidateDirectoryQuota(string sourceDir, CancellationToken cancellationToken)
        {
            if (!_quotaConfig.EnforceQuota) return;

            var totalSize = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                                    .Sum(f => new FileInfo(f).Length);
            await ValidateQuota(totalSize, cancellationToken);
        }

        protected virtual void OnQuotaWarningRaised(QuotaWarningEventArgs e)
        {
            ThrowIfDisposed();
            try
            {
                _logger.LogWarning("Storage quota warning: {Percentage}% used", e.UsagePercentage * 100);
                QuotaWarningRaised?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising quota warning event");
            }
        }

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

        private SemaphoreSlim GetFileLock(string path)
        {
            return _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        }


        private void UpdateCachedUsage(long deltaBytes)
        {
            if (_cachedUsage.HasValue)
            {
                _cachedUsage += deltaBytes;
            }
        }

        private void InitializeFileWatcher()
        {


            _watcher = new FileSystemWatcher(_rootPath)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName
                            | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite
                            | NotifyFilters.Size
            };

            _watcher.Created += (s, e) =>
            {
                OnFileSystemChanged(e.FullPath, FileSystemChangeType.Created);
                _cachedUsage = null;
            };
            _watcher.Deleted += (s, e) =>
            {
                OnFileSystemChanged(e.FullPath, FileSystemChangeType.Deleted);
                _cachedUsage = null;
            };
            _watcher.Changed += (s, e) =>
            {
                OnFileSystemChanged(e.FullPath, FileSystemChangeType.Modified);
                _cachedUsage = null;
            };

            _watcher.Created += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Created);
            _watcher.Deleted += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Deleted);
            _watcher.Changed += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Modified);
            _watcher.Renamed += (s, e) => OnFileSystemChanged(e.OldFullPath, FileSystemChangeType.Renamed, e.FullPath);
        }

        public async Task<long> GetAvailableSpace(CancellationToken cancellationToken = default)
        {
            var info = await GetQuotaInfo(cancellationToken);
            return _quotaConfig.MaxSizeBytes - info.CurrentUsageBytes;
        }

        protected virtual void OnFileSystemChanged(string path, FileSystemChangeType changeType, string? newPath = null)
        {
            try
            {
                _logger.LogInformation("File system change: {ChangeType} - {Path}", changeType, path);
                FileSystemChanged?.Invoke(this, new FileSystemChangeEventArgs(path, changeType, newPath));
                _cachedUsage = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising file system change event");
            }
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
            catch (QuotaExceededException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Operation cancelled for path: {Path}", path);
                throw;
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
            ThrowIfDisposed();
            LogOperation(nameof(ListContents), path);
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

        public async Task<Stream> OpenFile(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation(nameof(OpenFile), path);
            ValidatePathWithinRoot(path);
            ValidatePathLength(path);

            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            if (SecurityHelper.IsSymbolicLink(path))  // New security check
                throw new SecurityException("Symbolic links are not supported");

            SecurityHelper.ValidateFilePermissions(path);  // New security check

            return await WithFileSystemErrorHandling<Stream>(path, async () =>
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > ValidationConstants.MaxFileSize)
                    throw new IOException($"File size exceeds maximum allowed size of {ValidationConstants.MaxFileSize} bytes");

                if (fileInfo.Length > LargeFileThreshold) // Files larger than 100MB
                {
                    // Use FileStream directly for large files
                    return new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        ValidationConstants.MinBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                }
                else
                {
                    // Use MemoryStream for smaller files
                    var memoryStream = new MemoryStream((int)fileInfo.Length);
                    using var fileStream = File.OpenRead(path);
                    await fileStream.CopyToAsync(memoryStream, cancellationToken);
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
        private const int LargeFileThreshold = 100 * 1024 * 1024; // 100MB
        private const int DefaultBufferSize = 81920;
        private const int LargeBufferSize = 1024 * 1024; // 1MB
         

        public void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                // Cleanup any cached resources
                if (disposing)
                {
                    _watcher?.Dispose();
                    _watcher = null;
                    _cacheLock?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WindowsFileSystem));
        }

        


        public async Task WriteFile(string path, Stream content, int bufferSize = DefaultBufferSize, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation(nameof(WriteFile), path);
            ValidatePathWithinRoot(path);
            ValidatePathLength(path);     // New validation
            ValidateBufferSize(bufferSize); // New validation

            ArgumentNullException.ThrowIfNull(content);

            if (!content.CanRead)
                throw new ArgumentException("Stream must be readable", nameof(content));

            if (content.CanSeek)
            {
                await ValidateQuota(content.Length, cancellationToken);
            }
            else
            {
                // For non-seekable streams, use buffer size as minimum space requirement
                await ValidateQuota(bufferSize, cancellationToken);
                _logger.LogWarning("Writing non-seekable stream, quota check may be inaccurate");
            }

            var originalSize = File.Exists(path) ? new FileInfo(path).Length : 0;

            // Add directory creation if needed
            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                SecurityHelper.ValidateFilePermissions(Path.GetDirectoryName(path)!);  // New security check
                await CreateDirectory(Path.GetDirectoryName(path)!);
                await RetryOnIO(async () =>
                {
                    using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
                    await content.CopyToAsync(fileStream, bufferSize, cancellationToken);
                });
                var newSize = new FileInfo(path).Length;
                UpdateCachedUsage(newSize - originalSize);
                return Task.CompletedTask;
            });
            _cachedUsage = null;
        }

        public async Task CreateDirectory(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation(nameof(CreateDirectory), path);
            ValidatePath(path);
            ValidatePathWithinRoot(path);

            await RetryOnIO(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                return Task.CompletedTask;
            });
        }

        public async Task DeleteItem(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation(nameof(DeleteItem), path);
            ValidatePathWithinRoot(path);

            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new FileNotFoundException($"Path not found: {path}");

                if (Directory.Exists(path))
                    await DeleteDirectoryRecursive(path, cancellationToken);  // Added await here
                else
                    await RetryOnIO(() =>
                    {
                        File.Delete(path);
                        return Task.CompletedTask;
                    });
                _cachedUsage = null;
                return Task.CompletedTask;
            });
        }

        private static async Task DeleteDirectoryRecursive(string path, CancellationToken cancellationToken)
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteDirectoryRecursive(dir, cancellationToken);
            }
            foreach (var file in Directory.GetFiles(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RetryOnIO(() =>
                {
                    File.Delete(file);
                    return Task.CompletedTask;
                });
            }
            await RetryOnIO(() =>
            {
                Directory.Delete(path);
                return Task.CompletedTask;
            });
        }

        public async Task CopyItem(string sourcePath, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation(nameof(CopyItem), $"from {sourcePath} to {destinationPath}");
            ValidatePathWithinRoot(sourcePath);
            ValidatePathWithinRoot(destinationPath);

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                throw new FileNotFoundException($"Source path not found: {sourcePath}");

            var requiredSpace = File.Exists(sourcePath)? new FileInfo(sourcePath).Length: await GetDirectorySize(sourcePath, cancellationToken);

            await ValidateQuota(requiredSpace, cancellationToken);

            if (File.Exists(sourcePath))
            {
                var fileInfo = new FileInfo(sourcePath);
                await ValidateQuota(fileInfo.Length, cancellationToken);
            }
            else
            {
                await ValidateDirectoryQuota(sourcePath, cancellationToken);
            }

            if (Directory.Exists(sourcePath))
            {
                var dirSize = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories)
                                      .Sum(f => new FileInfo(f).Length);
                await ValidateQuota(dirSize, cancellationToken);
            }
            else if (File.Exists(sourcePath))
            {
                var fileInfo = new FileInfo(sourcePath);
                await ValidateQuota(fileInfo.Length, cancellationToken);
            }

            await Task.Run(async () =>  // Changed to async lambda
            {
                if (File.Exists(sourcePath))
                    await CopyFileWithProgress(sourcePath, destinationPath, progress, cancellationToken);
                else
                    await CopyDirectoryWithProgress(sourcePath, destinationPath, progress, cancellationToken);

                _cachedUsage = null;
            }, cancellationToken);
        }

        // CopyFileWithProgress and WriteFile need the same WinRT compatibility fixes
        private static async Task CopyFileWithProgress(string sourcePath, string destPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            const int bufferSize = LargeBufferSize; // 1MB buffer

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

        private async Task CopyDirectoryWithProgress(string sourceDir, string destDir, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var totalSize = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                           .Sum(f => new FileInfo(f).Length);
            await ValidateQuota(totalSize, cancellationToken);

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

        public async Task RenameItem(string oldPath, string newPath, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation(nameof(RenameItem), $"from {oldPath} to {newPath}");
            ValidatePath(oldPath);
            ValidatePath(newPath);
            ValidatePathWithinRoot(oldPath);  
            ValidatePathWithinRoot(newPath); 

            await WithFileSystemErrorHandling<Task>(oldPath, async () =>
            {
                if (!File.Exists(oldPath) && !Directory.Exists(oldPath))
                    throw new FileNotFoundException($"Path not found: {oldPath}");

                await RetryOnIO(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(oldPath))
                        File.Move(oldPath, newPath);
                    else
                        Directory.Move(oldPath, newPath);
                    _cachedUsage = null;
                    return Task.CompletedTask;
                });
                _cachedUsage = null;
                return Task.CompletedTask;
            });
        }

        public async Task<IEnumerable<FileSystemItem>> ListByType(string path, string extension)
        {
            ThrowIfDisposed();
            LogOperation(nameof(ListByType), $"{path} (*.{extension})");
            var contents = await ListContents(path);
            return contents.Where(f => !f.IsDirectory && f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        public Dictionary<string, string> GetFileProperties(string path)
        {
            ThrowIfDisposed();
            LogOperation(nameof(GetFileProperties), path);
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");

            var info = new FileInfo(path);
            return new Dictionary<string, string>
            {
                ["Size"] = info.Length.ToString(),
                ["Created"] = info.CreationTime.ToString(),
                ["Modified"] = info.LastWriteTime.ToString(),
                ["Attributes"] = info.Attributes.ToString(),
                ["IsReadOnly"] = info.IsReadOnly.ToString(),
                ["QuotaUsage"] = $"{_cachedUsage ?? 0L}/{_quotaConfig.MaxSizeBytes}",
                ["QuotaPercentage"] = $"{(_cachedUsage ?? 0L) * 100.0 / _quotaConfig.MaxSizeBytes:F1}%"
            };
        }
        private static void ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty", nameof(path));

            // Check for invalid characters
            if (Path.GetInvalidPathChars().Any(path.Contains))
                throw new ArgumentException("Path contains invalid characters", nameof(path));

            // Initial check for obvious path traversal attempts
            if (path.Contains('~') || path.Contains("../") || path.Contains("..\\"))
                throw new UnauthorizedAccessException("Path traversal attempt detected");

            // Normalize the path to prevent path traversal attacks
            try
            {
                // Get the full path and normalize it
                string fullPath = Path.GetFullPath(path);

                // Verify the path after normalization doesn't contain relative segments
                if (fullPath.Contains("..", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains("./", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains(".\\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Path contains invalid relative segments");
                }
            }
            catch (Exception ex) when (ex is not ArgumentException and not UnauthorizedAccessException)
            {
                throw new ArgumentException("Invalid path", nameof(path), ex);
            }
        }
        public async Task MigrateToNewRoot(string newRootPath, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    
    try
    {
        // Validate the new path
        Services.PathValidator.ValidateCustomRootPath(newRootPath);
        
        // Create the new root if it doesn't exist
        if (!Directory.Exists(newRootPath))
            Directory.CreateDirectory(newRootPath);
        
        // Migrate data
        await MigrateDataIfNeeded(_rootPath, newRootPath, cancellationToken);
        
        // Note: This doesn't change the current instance's root path
        // A new instance needs to be created with the new path
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to migrate data to new root path: {NewPath}", newRootPath);
        throw;
    }
}
        private async Task MigrateDataIfNeeded(string sourcePath, string destinationPath,
            CancellationToken cancellationToken = default)
        {
            if (sourcePath == destinationPath)
                return;

            if (!Directory.Exists(sourcePath))
                return;

            // Calculate total size
            var totalSize = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories)
                                 .Sum(f => new FileInfo(f).Length);

            // Check quota
            await ValidateQuota(totalSize, cancellationToken);

            // Create progress reporting (optional)
            var progress = new Progress<double>(p =>
                _logger.LogInformation("Migration progress: {Progress:P2}", p));

            // Copy all data
            foreach (var item in Directory.GetFileSystemEntries(sourcePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourcePath, item);
                var newPath = Path.Combine(destinationPath, relativePath);

                if (File.Exists(item))
                {
                    await RetryOnIO(() =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                        return Task.CompletedTask;
                    });
                    await CopyItem(item, newPath, progress, cancellationToken);
                }
                else if (Directory.Exists(item))
                {
                    // Create directory structure (missing in the second version)
                    await CreateDirectory(newPath, cancellationToken);
                }
            }

            _cachedUsage = null; // Invalidate cache after migration
        }
        private void LogOperation(string operation, string path)
        {
            _logger.LogInformation("File operation: {Operation} on {Path}", operation, path);
        }

        private static void ValidateBufferSize(int bufferSize)
        {
            if (bufferSize < ValidationConstants.MinBufferSize || bufferSize > ValidationConstants.MaxBufferSize)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSize),
                    $"Buffer size must be between {ValidationConstants.MinBufferSize} and {ValidationConstants.MaxBufferSize} bytes");
            }
        }
        private static void ValidatePathLength(string path)
        {
            if (path.Length > ValidationConstants.MaxPathLength)
            {
                throw new PathTooLongException(
                    $"Path length exceeds maximum allowed length of {ValidationConstants.MaxPathLength} characters");
            }
        }

        private static void ValidateProgress(IProgress<double>? progress, double value)
        {
            if (progress != null && value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    "Progress value must be between 0 and 1");
            }
        }
    }
}

