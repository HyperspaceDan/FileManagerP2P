// Platforms/Android/AndroidFileSystem.cs
using Java.IO;
using Android.OS;
using Android.App;
using Android.Net;
using Android.Content.PM;
using static Android.Manifest;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FileManager.Core.Interfaces;
using FileManager.Core.Exceptions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using FileManager.Core.Models;
using Microsoft.Maui.Storage;
using Android.Content;
using Android.Content.Res;
using Android.App.Usage;
using FileManagerP2P.Services;
using Android.Database;
using Android.Provider;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidActivity = Android.App.Activity;
using static Android.Preferences.PreferenceManager;

namespace FileManagerP2P.Platforms.Android
{
    /// <summary>
    /// Android file system implementation with support for various Android versions including Android 14 (API 34).
    /// 
    /// Key Android version considerations:
    /// - Android 14+ (API 34): Introduces READ_MEDIA_VISUAL_USER_SELECTED permission for partial access
    ///   and recommends Photo Picker API for media file access.
    /// - Android 13+ (API 33): Uses granular media permissions (READ_MEDIA_IMAGES, READ_MEDIA_VIDEO, READ_MEDIA_AUDIO)
    /// - Android 11+ (API 30): Uses MANAGE_EXTERNAL_STORAGE permission for full file access
    /// - Android 10+ (API 29): Introduces Scoped Storage, limiting direct file access
    /// - Android 6.0-9.0: Uses runtime READ/WRITE_EXTERNAL_STORAGE permissions
    /// - Android prior to 6.0: Uses manifest-declared storage permissions
    /// </summary>
    public class AndroidFileSystem : FileManager.Core.Interfaces.IFileSystem, IDisposable, IAsyncDisposable
    {
        #region Constants
        private const int DefaultBufferSize = 81920;
        private const int LargeFileThreshold = 100 * 1024 * 1024; // 100MB
        private static readonly TimeSpan UsageCacheDuration = TimeSpan.FromMinutes(5);
        private const int ANDROID_14_API_LEVEL = 34;  // Android 14 (API 34)
        #endregion

        #region Fields
        private readonly Context _context;
        private readonly ILogger<AndroidFileSystem> _logger;
        private readonly string _rootPath;
        private readonly QuotaConfiguration _quotaConfig;
        private long? _cachedUsage;
        private DateTime _lastUsageCheck = DateTime.MinValue;
        private bool _disposed;
        private FileObserver? _fileObserver;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
        private readonly ReaderWriterLockSlim _cacheLock = new(LockRecursionPolicy.NoRecursion);
        private string? _movedFromPath;
        private ContentObserver? _contentObserver;
        private readonly Handler _handler;
        #endregion

        #region Events
        public event EventHandler<FileSystemChangeEventArgs>? FileSystemChanged;
        public event EventHandler<QuotaWarningEventArgs>? QuotaWarningRaised;
        #endregion

        protected virtual void OnFileSystemChanged(string path, FileSystemChangeType changeType, string? newPath = null)
        {
            ThrowIfDisposed();

            try
            {
                _logger.LogInformation("File system change: {ChangeType} - {Path}", changeType, path);
                FileSystemChanged?.Invoke(this, new FileSystemChangeEventArgs(path, changeType, newPath));
                InvalidateUsageCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error raising file system change event");
            }
        }


        #region Factory Methods
        public static async Task<AndroidFileSystem> CreateWithSecurePathAsync(
            ILogger<AndroidFileSystem> logger,
            IFileSystemPathProvider pathProvider)
        {
            ArgumentNullException.ThrowIfNull(pathProvider);

            var rootPath = await pathProvider.GetRootPathAsync();

            return new AndroidFileSystem(logger, rootPath, new QuotaConfiguration
            {
                MaxSizeBytes = 1024 * 1024 * 1024, // 1GB default
                RootPath = rootPath,
                WarningThreshold = 0.9f,
                EnforceQuota = true
            });
        }
        public static async Task<AndroidFileSystem> CreateWithSecurePathAsync(
            ILogger<AndroidFileSystem> logger,
            IFileSystemPathProvider pathProvider,
            QuotaConfiguration baseConfig)
        {
            ArgumentNullException.ThrowIfNull(pathProvider);
            ArgumentNullException.ThrowIfNull(baseConfig);

            var rootPath = await pathProvider.GetRootPathAsync();

            var quotaConfig = new QuotaConfiguration
            {
                MaxSizeBytes = baseConfig.MaxSizeBytes,
                RootPath = rootPath,
                WarningThreshold = baseConfig.WarningThreshold,
                EnforceQuota = baseConfig.EnforceQuota
            };

            return new AndroidFileSystem(logger, rootPath, quotaConfig);
        }
        #endregion
        #region Constructors
        public AndroidFileSystem(ILogger<AndroidFileSystem> logger)
            : this(logger, FileSystem.AppDataDirectory, new QuotaConfiguration
            {
                MaxSizeBytes = 1024 * 1024 * 1024, // 1GB default
                RootPath = FileSystem.AppDataDirectory,
                WarningThreshold = 0.9f,
                EnforceQuota = true
            })
        {
        }
        public AndroidFileSystem(ILogger<AndroidFileSystem> logger, string rootPath, QuotaConfiguration quotaConfig)
        {
            ArgumentNullException.ThrowIfNull(quotaConfig);
            if (quotaConfig.MaxSizeBytes <= 0)
                throw new ArgumentException("MaxSizeBytes must be positive", nameof(quotaConfig));
            if (quotaConfig.WarningThreshold <= 0 || quotaConfig.WarningThreshold > 1)
                throw new ArgumentException("WarningThreshold must be between 0 and 1", nameof(quotaConfig));

            _context = global::Android.App.Application.Context
                ?? throw new InvalidOperationException("Application context is not available");
            _logger = logger;
            _rootPath = Path.GetFullPath(rootPath);
            _quotaConfig = quotaConfig;
            _handler = new Handler(Looper.MainLooper ?? throw new InvalidOperationException("Main looper is not available"));
            _contentObserver = new FileContentObserver(_handler, this);
            var contentUri = MediaStore.Files.GetContentUri("external") ?? throw new InvalidOperationException("Failed to get external storage content URI");
            _context.ContentResolver?.RegisterContentObserver(
                contentUri,
                true,
                _contentObserver);

            InitializeFileObserver();

            EnsureStoragePermissions();

            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
            }
        }
        #endregion

        #region Permissions Management
        private void EnsureStoragePermissions()
        {
            try
            {
                // Check for Android 14 (API 34) specific handling
                if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)  // Android 14
                {
                    _logger.LogInformation("Running on Android 14 or higher (API level {ApiLevel})", Build.VERSION.SdkInt);

                    // Android 14 has introduced partial access for granular media permissions
                    // Check if we need to use Photo Picker API instead for media access
                    bool isAccessingAppDirectory = _rootPath.StartsWith(FileSystem.AppDataDirectory, StringComparison.OrdinalIgnoreCase);

                    if (!isAccessingAppDirectory)
                    {
                        var readMediaImagesPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                            _context, global::Android.Manifest.Permission.ReadMediaImages);

                        var readMediaVideoPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                            _context, global::Android.Manifest.Permission.ReadMediaVideo);

                        var readMediaAudioPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                            _context, global::Android.Manifest.Permission.ReadMediaAudio);

                        // New in Android 14: Permission for visual media only (photos and videos but not audio)
                        var readMediaVisualUserSelected = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                            _context, "android.permission.READ_MEDIA_VISUAL_USER_SELECTED");

                        // Log permission status with named parameters for better readability
                        _logger.LogInformation(
                            "Android 14+ permissions status - " +
                            "Images: {ImagesPermission}, " +
                            "Video: {VideoPermission}, " +
                            "Audio: {AudioPermission}, " +
                            "VisualUserSelected: {VisualUserSelectedPermission}",
                            readMediaImagesPermission,
                            readMediaVideoPermission,
                            readMediaAudioPermission,
                            readMediaVisualUserSelected);

                        // Check if any of the permissions are granted
                        bool hasAnyMediaPermission =
                            readMediaVisualUserSelected == global::Android.Content.PM.Permission.Granted ||
                            readMediaImagesPermission == global::Android.Content.PM.Permission.Granted ||
                            readMediaVideoPermission == global::Android.Content.PM.Permission.Granted ||
                            readMediaAudioPermission == global::Android.Content.PM.Permission.Granted;

                        // For paths outside our app directory where we need media access but have no permissions
                        if (!hasAnyMediaPermission && !global::Android.OS.Environment.IsExternalStorageManager)
                        {
                            _logger.LogWarning("No media permissions granted on Android 14+");
                            // Note: On Android 14 we should use the Photo Picker API for user-selected media
                            // instead of throwing an exception, but for full file system access, we still need permissions

                            throw new UnauthorizedAccessException(
                                "On Android 14+, the app requires either granular media permissions, " +
                                "MANAGE_EXTERNAL_STORAGE permission, or use of the Photo Picker API " +
                                "for accessing media files.");
                        }

                        return; // Return if we're on Android 14+ with appropriate permissions
                    }
                }


                // Add Android 13+ (API 33) granular media permission checks
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) // Android 13 (API 33)
                {
                    var readMediaImagesPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                        _context,
                        global::Android.Manifest.Permission.ReadMediaImages);

                    var readMediaVideoPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                        _context,
                        global::Android.Manifest.Permission.ReadMediaVideo);

                    var readMediaAudioPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                        _context,
                        global::Android.Manifest.Permission.ReadMediaAudio);

                    bool hasGranularPermissions = (readMediaImagesPermission == global::Android.Content.PM.Permission.Granted &&
                                          readMediaVideoPermission == global::Android.Content.PM.Permission.Granted &&
                                          readMediaAudioPermission == global::Android.Content.PM.Permission.Granted);

                    if (!hasGranularPermissions)
                    {
                        _logger.LogWarning("One or more Android 13+ granular media permissions not granted");

                        // Log the specific missing permissions
                        if (readMediaImagesPermission != global::Android.Content.PM.Permission.Granted)
                            _logger.LogWarning("READ_MEDIA_IMAGES permission not granted");
                        if (readMediaVideoPermission != global::Android.Content.PM.Permission.Granted)
                            _logger.LogWarning("READ_MEDIA_VIDEO permission not granted");
                        if (readMediaAudioPermission != global::Android.Content.PM.Permission.Granted)
                            _logger.LogWarning("READ_MEDIA_AUDIO permission not granted");

                        _logger.LogInformation("Granular media permissions status - Images: {ImagesPermission}, Video: {VideoPermission}, Audio: {AudioPermission}",
                        readMediaImagesPermission, readMediaVideoPermission, readMediaAudioPermission);

                        // On Android 13+, we must check for Storage Manager permissions if we need full access
                        // or check legacy storage permissions if we're within our own app directory
                        bool isAccessingAppDirectory = _rootPath.StartsWith(FileSystem.AppDataDirectory, StringComparison.OrdinalIgnoreCase);

                        if (!isAccessingAppDirectory)
                        {
                            // For paths outside our app directory, we need MANAGE_EXTERNAL_STORAGE on Android 11+
                            if (OperatingSystem.IsAndroidVersionAtLeast(30) && !global::Android.OS.Environment.IsExternalStorageManager)
                            {
                                throw new UnauthorizedAccessException(
                                    "App requires either granular media permissions or MANAGE_EXTERNAL_STORAGE permission for accessing media files. " +
                                    "Please grant 'All Files Access' permission in Settings.");
                            }
                        }
                        // Otherwise, we're in app directory which should be accessible, continue with legacy checks
                    }
                    else
                    {
                        // We have all the granular permissions, we can skip other permission checks
                        // for media files (but might still need checks for non-media files)
                        _logger.LogInformation("All Android 13+ granular media permissions granted");
                        return;
                    }
                }

                if (Build.VERSION.SdkInt >= BuildVersionCodes.R) //Android 11 (API 30) and above
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    {
                        if (!global::Android.OS.Environment.IsExternalStorageManager)
                        {
                            _logger.LogWarning("MANAGE_EXTERNAL_STORAGE permission not granted");
                            throw new UnauthorizedAccessException(
                                "App requires MANAGE_EXTERNAL_STORAGE permission. Please grant 'All Files Access' permission in Settings.");
                        }
                    }
                    else
                    {
                        // Fallback for Android versions that don't support IsExternalStorageManager
                        CheckLegacyStoragePermissions();
                    }
                }
                else
                {
                    CheckLegacyStoragePermissions();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage permission check failed");
                throw;
            }
        }
        private void CheckLegacyStoragePermissions()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M) // Android 6.0 (API 23) and above
            {
                var readPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                    _context,
                    global::Android.Manifest.Permission.ReadExternalStorage);

                var writePermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                    _context,
                    global::Android.Manifest.Permission.WriteExternalStorage);

                if (readPermission != global::Android.Content.PM.Permission.Granted ||
                    writePermission != global::Android.Content.PM.Permission.Granted)
                {
                    _logger.LogWarning("READ/WRITE_EXTERNAL_STORAGE permissions not granted");
                    throw new UnauthorizedAccessException("App requires READ_EXTERNAL_STORAGE and WRITE_EXTERNAL_STORAGE permissions");
                }
            }
            else // Android 5.x (API 21-22)
            {
                // Before Android 6.0, permissions were granted at install time
                // Just verify if the permissions are declared in the manifest
                var packageInfo = _context.PackageManager?.GetPackageInfo(_context.PackageName!, PackageInfoFlags.Permissions);
                var declaredPermissions = packageInfo?.RequestedPermissions;

                if (declaredPermissions == null ||
                    !declaredPermissions.Contains(global::Android.Manifest.Permission.ReadExternalStorage) ||
                    !declaredPermissions.Contains(global::Android.Manifest.Permission.WriteExternalStorage))
                {
                    _logger.LogWarning("Storage permissions not declared in manifest");
                    throw new UnauthorizedAccessException(
                        "Storage permissions are not declared in the manifest");
                }
            }
        }
        #endregion

        #region Helper Methods
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

        private void LogOperation(string operation, string path)
        {
            _logger.LogInformation("File operation: {Operation} on {Path}", operation, path);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AndroidFileSystem));
        }

        private static void ValidateBufferSize(int bufferSize)
        {
            if (bufferSize < 4096 || bufferSize > 8388608) // 4KB to 8MB
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSize),
                    $"Buffer size must be between 4KB and 8MB bytes");
            }
        }
        private static void ValidatePathLength(string path)
        {
            if (path.Length > 260) // Standard MAX_PATH value
            {
                throw new PathTooLongException(
                    $"Path length exceeds maximum allowed length of 260 characters");
            }
        }

        private SemaphoreSlim GetFileLock(string path)
        {
            return _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
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
            catch (System.OperationCanceledException)
            {
                _logger.LogWarning("Operation cancelled for path: {Path}", path);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied to path: {Path}", path);
                throw new UnauthorizedAccessException($"Access denied to path: {path}", ex);
            }
            catch (Java.IO.IOException ex)
            {
                _logger.LogError(ex, "IO error accessing path: {Path}", path);
                throw new System.IO.IOException($"IO error accessing path: {path}", ex);
            }
            catch (Exception ex) when (ex is not System.OperationCanceledException)
            {
                _logger.LogError(ex, "Error accessing path: {Path}", path);
                throw new System.IO.IOException($"Error accessing path: {path}", ex);
            }
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
                catch (Java.IO.IOException) when (i < maxRetries - 1)
                {
                    await Task.Delay((i + 1) * 100); // Exponential backoff
                }
            }
        }
        #endregion

        #region Quota Management
        public async Task<QuotaInfo> GetQuotaInfo(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var currentUsage = await GetCurrentUsageInternal(cancellationToken);
            var percentage = (float)currentUsage / _quotaConfig.MaxSizeBytes;

            return new QuotaInfo(
                currentUsage,
                _quotaConfig.MaxSizeBytes,
                percentage,
                currentUsage >= _quotaConfig.MaxSizeBytes
            );
        }
        public async Task ValidateQuota(long requiredBytes, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!_quotaConfig.EnforceQuota) return;

            var info = await GetQuotaInfo(cancellationToken);
            var projectedUsage = info.CurrentUsageBytes + requiredBytes;

            if (projectedUsage >= _quotaConfig.MaxSizeBytes)
            {
                _logger.LogWarning("Quota would be exceeded: Required {RequiredBytes} bytes, Available {AvailableBytes} bytes",
                    requiredBytes, _quotaConfig.MaxSizeBytes - info.CurrentUsageBytes);

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
            ThrowIfDisposed();

            if (!_quotaConfig.EnforceQuota) return;

            long totalSize = await Task.Run(() =>
            {
                var directory = new Java.IO.File(sourceDir);
                return GetDirectorySizeRecursive(directory);
            }, cancellationToken);

            await ValidateQuota(totalSize, cancellationToken);
        }

        private void InvalidateUsageCache()
        {
            ThrowIfDisposed();

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
            ThrowIfDisposed();

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
                var rootDir = new Java.IO.File(_rootPath);
                return GetDirectorySizeRecursive(rootDir);
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
        private static long GetDirectorySizeRecursive(Java.IO.File directory)
        {
            if (!directory.Exists() || !directory.IsDirectory)
                return 0;

            long size = 0;
            var files = directory.ListFiles();

            if (files == null)
                return 0;

            foreach (var file in files)
            {
                if (file.IsDirectory)
                {
                    size += GetDirectorySizeRecursive(file);
                }
                else
                {
                    size += file.Length();
                }
            }

            return size;
        }
        public async Task<long> GetAvailableSpace(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var info = await GetQuotaInfo(cancellationToken);
            return _quotaConfig.MaxSizeBytes - info.CurrentUsageBytes;
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
        private void UpdateCachedUsage(long deltaBytes)
        {
            ThrowIfDisposed();

            if (_cachedUsage.HasValue)
            {
                try
                {
                    _cacheLock.EnterWriteLock();
                    _cachedUsage += deltaBytes;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
        }
        #endregion
        #region File Observer
        private class FileContentObserver(Handler handler, AndroidFileSystem fileSystem, string? observedPath = null)
        : ContentObserver(handler)
        {
            private readonly AndroidFileSystem _fileSystem = fileSystem;
            private readonly string _observedPath = observedPath ?? fileSystem._rootPath;
            private readonly HashSet<string> _knownMimeTypes =
            [
                "application/octet-stream",
                "text/plain",
                "application/pdf",
                "image/jpeg",
                "image/png",
                "video/mp4",
                "audio/mp3"
            ];

            public override void OnChange(bool selfChange, global::Android.Net.Uri? uri)
            {
                base.OnChange(selfChange, uri);

                // Skip null URIs
                if (uri == null) return;

                try
                {
                    // Filter by path if available
                    string? uriPath = GetActualPathFromUri(uri);
                    if (string.IsNullOrEmpty(uriPath)) return;

                    // Check if the path is within our monitored directory
                    if (!uriPath.StartsWith(_observedPath, StringComparison.OrdinalIgnoreCase))
                        return;

                    // Get more details about the file using query
                    var changeType = DetermineChangeType(uri);

                    _fileSystem.OnFileSystemChanged(uriPath, changeType);
                    _fileSystem.InvalidateUsageCache();
                }
                catch (Exception ex)
                {
                    _fileSystem._logger.LogError(ex, "Error handling ContentObserver change for URI: {Uri}", uri);
                }
            }
            private string? GetActualPathFromUri(global::Android.Net.Uri uri)
            {
                // Handle different URI schemes
                if ("file".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return uri.Path;
                }

                // For content:// URIs, try to resolve to a file path
                if ("content".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Query file details
                        var projection = new[] { global::Android.Provider.MediaStore.IMediaColumns.Data };
                        var cursor = _fileSystem._context.ContentResolver?.Query(uri, projection, null, null, null);

                        using (cursor)
                        {
                            if (cursor?.MoveToFirst() == true)
                            {
                                var columnIndex = cursor.GetColumnIndexOrThrow(global::Android.Provider.MediaStore.IMediaColumns.Data);
                                return cursor.GetString(columnIndex);
                            }
                        }

                        // Alternate method: try to get path from URI
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.Kitkat)
                        {
                            var docId = global::Android.Provider.DocumentsContract.GetDocumentId(uri);
                            if (!string.IsNullOrEmpty(docId))
                            {
                                // Extract path from document ID for external storage
                                if (docId.StartsWith("primary:"))
                                {
                                    return Path.Combine(
                                        GetExternalStoragePath(),
                                        docId["primary".Length..]);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _fileSystem._logger.LogWarning(ex, "Failed to resolve content URI path: {Uri}", uri);
                    }
                }

                return uri.Path;
            }
            private FileSystemChangeType DetermineChangeType(global::Android.Net.Uri uri)
            {
                try
                {
                    // Default to Modified
                    var changeType = FileSystemChangeType.Modified;

                    // For newer Android versions, try to determine the change type from the URI
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.R) // Android 11+
                    {
                        var cursor = _fileSystem._context.ContentResolver?.Query(
                            uri,
                            [ global :: Android.Provider.MediaStore.IMediaColumns.DateAdded,
                              global :: Android.Provider.MediaStore.IMediaColumns.DateModified,
                            //fallback cloumns for older android versions
                            "date_added", "date_modified"
                            ],
                            null, null, null);

                        using (cursor)
                        {
                            if (cursor?.MoveToFirst() == true)
                            {
                                var dateAddedIdx = cursor.GetColumnIndex(global::Android.Provider.MediaStore.IMediaColumns.DateAdded);
                                var dateModifiedIdx = cursor.GetColumnIndex(global::Android.Provider.MediaStore.IMediaColumns.DateModified);

                                // Try fallbacks if needed
                                if (dateAddedIdx == -1)
                                    dateAddedIdx = cursor.GetColumnIndex("date_added");

                                if (dateModifiedIdx == -1)
                                    dateModifiedIdx = cursor.GetColumnIndex("date_modified");

                                if (dateAddedIdx != -1 && dateModifiedIdx != -1)
                                {
                                    var dateAdded = cursor.GetLong(dateAddedIdx);
                                    var dateModified = cursor.GetLong(dateModifiedIdx);

                                    // If dates are very close, it's likely a new file
                                    if (Math.Abs(dateModified - dateAdded) < 5)
                                    {
                                        changeType = FileSystemChangeType.Created;
                                    }
                                    else
                                    {
                                        changeType = FileSystemChangeType.Modified;
                                    }
                                }
                            }
                        }
                    }

                    return changeType;
                }
                catch (Exception ex)
                {
                    _fileSystem._logger.LogWarning(ex, "Failed to determine change type: {Uri}", uri);
                    return FileSystemChangeType.Modified; // Default
                }
            }
        }


        private void InitializeFileObserver()
        {
            try
            {
                // Android's FileObserver requires a valid path
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) // Android 10+
                {
                    // Use ContentObserver for newer Android versions
                    _contentObserver = new FileContentObserver(_handler, this, _rootPath);

                    // Register with more specific content URIs for better filtering
                    var resolver = _context.ContentResolver;

                    // External media files
                    var externalUri = MediaStore.Files.GetContentUri("external");
                    if(externalUri != null)
                        resolver?.RegisterContentObserver(externalUri, true, _contentObserver);
                    else
                        _logger.LogWarning("Failed to get external storage content URI");

                    // Internal media files
                    var internalUri = MediaStore.Files.GetContentUri("internal");
                    if (internalUri != null)
                    {
                        resolver?.RegisterContentObserver(internalUri, true, _contentObserver);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get internal storage content URI");
                    }

                    var imagesUri = MediaStore.Images.Media.ExternalContentUri;
                    if (imagesUri != null)
                    {
                        resolver?.RegisterContentObserver(imagesUri, true, _contentObserver);
                    }

                    // Videos
                    var videosUri = MediaStore.Video.Media.ExternalContentUri;
                    if (videosUri != null)
                    {
                        resolver?.RegisterContentObserver(videosUri, true, _contentObserver);
                    }

                    // Downloads
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                    {
                        var downloadsUri = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternal);
                        if (downloadsUri != null)
                        {
                            resolver?.RegisterContentObserver(downloadsUri, true, _contentObserver);
                        } 
                        else
                        {
                            _logger.LogWarning("Failed to get downloads content URI");
                        }
                    }
                    _logger.LogInformation("ContentObserver registered for multiple media URIs");
                }
                else
                {
                    // Use FileObserver for older Android versions
                    _fileObserver = new FileChangesObserver(_rootPath, this);
                    _fileObserver.StartWatching();
                    _logger.LogInformation("FileObserver started for path: {Path}", _rootPath);
                }

                _logger.LogInformation("File observer initialized for path: {Path}", _rootPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize file observer");
            }
        }

        private class FileChangesObserver(string path, AndroidFileSystem fileSystem) : FileObserver(
        path,
        FileObserverEvents.Create |
        FileObserverEvents.Delete |
        FileObserverEvents.Modify |
        FileObserverEvents.MovedFrom |
        FileObserverEvents.MovedTo)
        {
            private readonly string _observedPath = path;
            private readonly AndroidFileSystem _fileSystem = fileSystem;
            
            public override void OnEvent(FileObserverEvents e, string? path)
            {
                if (string.IsNullOrEmpty(path)) return;

                var fullPath = Path.Combine(_observedPath, path);

                switch (e)
                {
                    case FileObserverEvents.Create:
                        _fileSystem.OnFileSystemChanged(fullPath, FileSystemChangeType.Created);
                        break;
                    case FileObserverEvents.Delete:
                        _fileSystem.OnFileSystemChanged(fullPath, FileSystemChangeType.Deleted);
                        break;
                    case FileObserverEvents.Modify:
                        _fileSystem.OnFileSystemChanged(fullPath, FileSystemChangeType.Modified);
                        break;
                    case FileObserverEvents.MovedFrom:
                        // Store the "from" path for later use with MovedTo event
                        _fileSystem._movedFromPath = fullPath;
                        break;
                    case FileObserverEvents.MovedTo:
                        _fileSystem.OnFileSystemChanged(_fileSystem._movedFromPath ?? string.Empty,
                            FileSystemChangeType.Renamed, fullPath);
                        _fileSystem._movedFromPath = null;
                        break;
                }

                // Invalidate cache when files change
                _fileSystem.InvalidateUsageCache();
            }
        }
        #endregion

        #region IDisposable Implementation
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Dispose managed resources
                _fileObserver?.Dispose();
                _cacheLock?.Dispose();

                // Unregister content observer
            if (_contentObserver != null && _context?.ContentResolver != null)
            {
                try
                {
                    _context.ContentResolver.UnregisterContentObserver(_contentObserver);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unregister content observer");
                }
            }

                // Release semaphores
                foreach (var semaphore in _fileLocks.Values)
                {
                    semaphore.Dispose();
                }
                _fileLocks.Clear();
            }

            _disposed = true;
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
        #endregion
                

        public async Task<IEnumerable<FileSystemItem>> ListContents(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsureStoragePermissions();
            LogOperation(nameof(ListContents), path);
            ValidatePathWithinRoot(path);
            return await WithFileSystemErrorHandling<IEnumerable<FileSystemItem>>(path, async () =>
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = new Java.IO.File(path);
                    if (!directory.Exists())
                        throw new System.IO.DirectoryNotFoundException($"Directory not found: {path}");

                    var files = directory.ListFiles();
                    if (files == null) return Enumerable.Empty<FileSystemItem>();

                    var fileSystemItems = new List<FileSystemItem>(files.Length);
                    var parallelOptions = new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount / 2)
                    };

                    Parallel.ForEach(files, parallelOptions, file =>
                    {
                        try
                        {
                            var fileSystemItem = new FileSystemItem
                            {
                                Name = file.Name,
                                Path = file.AbsolutePath,
                                IsDirectory = file.IsDirectory,
                                Size = file.IsDirectory ? 0 : file.Length(),
                                ModifiedDate = DateTimeOffset.FromUnixTimeMilliseconds(file.LastModified()).DateTime
                            };

                            lock (fileSystemItems)
                            {
                                fileSystemItems.Add(fileSystemItem);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing file: {FilePath}", file.AbsolutePath);
                        }
                    });

                    return fileSystemItems.OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name);
                }, cancellationToken);
            });
        }

        public async Task<Stream> OpenFile(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsureStoragePermissions();
            LogOperation(nameof(OpenFile), path);
            ValidatePathWithinRoot(path);
            ValidatePathLength(path);

            return await WithFileSystemErrorHandling<Stream>(path, async () =>
            {
                var file = new Java.IO.File(path);
                if (!file.Exists())
                    throw new System.IO.FileNotFoundException($"File not found: {path}");

                if (file.Length() > LargeFileThreshold)
                {
                    // Return a FileStream for large files
                    return new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        DefaultBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                }
                else
                {
                    // Return a MemoryStream for smaller files
                    var memoryStream = new MemoryStream((int)file.Length());
                    using var fileStream = System.IO.File.OpenRead(path);
                    await fileStream.CopyToAsync(memoryStream, DefaultBufferSize, cancellationToken);
                    memoryStream.Position = 0;
                    return memoryStream;
                }
            });
        }

        public async Task WriteFile(string path, Stream content, int bufferSize = DefaultBufferSize, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsureStoragePermissions();
            LogOperation(nameof(WriteFile), path);
            ValidatePathWithinRoot(path);
            ValidatePathLength(path);
            ValidateBufferSize(bufferSize);

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

            var originalSize = 0L;
            if (System.IO.File.Exists(path))
            {
                var file = new Java.IO.File(path);
                originalSize = file.Length();
            }

            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                // Create parent directory if needed
                var dirPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
                {
                    await CreateDirectory(dirPath, cancellationToken);
                }

                await RetryOnIO(async () =>
                {
                    using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
                    await content.CopyToAsync(fileStream, bufferSize, cancellationToken);
                });

                var newSize = new Java.IO.File(path).Length();
                UpdateCachedUsage(newSize - originalSize);
                return Task.CompletedTask;
            });
        }

        public async Task CreateDirectory(string path, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            ThrowIfDisposed();
            LogOperation(nameof(CreateDirectory), path);
            ValidatePathWithinRoot(path);
            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                await RetryOnIO(() =>
                {
                    var dir = new Java.IO.File(path);
                    if (!dir.Exists() && !dir.Mkdirs())
                    {
                        throw new System.IO.IOException($"Failed to create directory at {path}");
                    }
                    return Task.CompletedTask;
                });
                return Task.CompletedTask;
            });
        }

        public async Task DeleteItem(string path, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            ThrowIfDisposed();
            LogOperation(nameof(DeleteItem), path);
            ValidatePathWithinRoot(path);
            await WithFileSystemErrorHandling<Task>(path, async () =>
            {
                var file = new Java.IO.File(path);
                if (!file.Exists())
                    throw new System.IO.FileNotFoundException($"Path not found: {path}");

                long sizeToRemove = 0;
                if (file.IsDirectory)
                {
                    sizeToRemove = await Task.Run(() => GetDirectorySizeRecursive(file), cancellationToken);
                    await Task.Run(() => DeleteRecursive(file, cancellationToken), cancellationToken);
                }
                else
                {
                    sizeToRemove = file.Length();
                    if (!file.Delete())
                        throw new System.IO.IOException($"Failed to delete file: {path}");
                }

                UpdateCachedUsage(-sizeToRemove);
                return Task.CompletedTask;
            });
        }

        public async Task CopyItem(string sourcePath, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            ThrowIfDisposed();
            LogOperation(nameof(CopyItem), $"from {sourcePath} to {destinationPath}");
            ValidatePathWithinRoot(sourcePath);
            ValidatePathWithinRoot(destinationPath);


            var source = new Java.IO.File(sourcePath);
            if (!source.Exists())
                throw new System.IO.FileNotFoundException($"Source path not found: {sourcePath}");

            // Estimate required space
            long requiredSpace;
            if (source.IsDirectory)
            {
                requiredSpace = await Task.Run(() => GetDirectorySizeRecursive(source), cancellationToken);
            }
            else
            {
                requiredSpace = source.Length();
            }

            // Check quota before operation
            await ValidateQuota(requiredSpace, cancellationToken);

            await WithFileSystemErrorHandling<Task>(sourcePath, async () =>
            {
                // Create parent directory if needed
                var destParent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
                {
                    await CreateDirectory(destParent, cancellationToken);
                }

                await Task.Run(() =>
                {
                    var dest = new Java.IO.File(destinationPath);
                    if (source.IsFile)
                        CopyFile(source, dest, progress, cancellationToken);
                    else
                        CopyDirectory(source, dest, progress, cancellationToken);
                }, cancellationToken);

                // Update cache after successful operation
                UpdateCachedUsage(requiredSpace);

                return Task.CompletedTask;
            });

        }

        public async Task RenameItem(string oldPath, string newPath, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            ThrowIfDisposed();
            LogOperation(nameof(RenameItem), $"from {oldPath} to {newPath}");
            ValidatePathWithinRoot(oldPath);
            ValidatePathWithinRoot(newPath);
            await WithFileSystemErrorHandling<Task>(oldPath, async () =>
            {
                var oldFile = new Java.IO.File(oldPath);
                if (!oldFile.Exists())
                    throw new System.IO.FileNotFoundException($"File not found: {oldPath}");

                // Create parent directory of destination if needed
                var destParent = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
                {
                    await CreateDirectory(destParent, cancellationToken);
                }

                await RetryOnIO(() =>
                {
                    if (!oldFile.RenameTo(new Java.IO.File(newPath)))
                        throw new System.IO.IOException($"Failed to rename {oldPath} to {newPath}");
                    return Task.CompletedTask;
                });

                return Task.CompletedTask;
            });
        }

        private static void DeleteRecursive(Java.IO.File file, CancellationToken cancellationToken = default)
        {
            if (file.IsDirectory)
            {
                var children = file.ListFiles();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DeleteRecursive(child, cancellationToken);
                    }
                }
            }
            file.Delete();
        }

        private static void CopyFile(Java.IO.File source, Java.IO.File dest, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            using var inStream = new FileInputStream(source);
            using var outStream = new FileOutputStream(dest);
            var inChannel = inStream.Channel;
            var outChannel = outStream.Channel;

            if (inChannel != null && outChannel != null)
            {
                long size = inChannel.Size();
                long position = 0;
                const int chunkSize = 1024 * 1024; // 1MB chunks

                while (position < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long remainingSize = size - position;
                    long transferSize = Math.Min(remainingSize, chunkSize);
                    position += inChannel.TransferTo(position, transferSize, outChannel);
                    progress?.Report((double)position / size);
                }
            }
            else
            {
                throw new InvalidOperationException("File channels cannot be null");
            }
        }

        private static void CopyDirectory(Java.IO.File source, Java.IO.File dest, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            dest.Mkdirs();
            var files = source.ListFiles();
            if (files != null)
            {
                int totalFiles = files.Length;
                int currentFile = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var newFile = new Java.IO.File(dest, file.Name);
                    if (file.IsDirectory)
                        CopyDirectory(file, newFile, progress, cancellationToken);
                    else
                    {
                        CopyFile(file, newFile, progress, cancellationToken);
                        currentFile++;
                        progress?.Report((double)currentFile / totalFiles);
                    }
                }
            }
        }

        public static string GetExternalStoragePath()
        {
            var externalStorageState = global::Android.OS.Environment.ExternalStorageState;
            if (externalStorageState != global::Android.OS.Environment.MediaMounted)
                throw new InvalidOperationException($"External storage is not mounted. Current state: {externalStorageState}");

            var externalStorageDirectory = global::Android.OS.Environment.ExternalStorageDirectory
                ?? throw new InvalidOperationException("External storage directory is not available.");

            return externalStorageDirectory.AbsolutePath;
        }

        public static bool IsAccessible(string path)
        {
            var uri = global::Android.Net.Uri.Parse(path);
            var context = global::Android.App.Application.Context;
            return context?.ContentResolver?.PersistedUriPermissions
                .Any(p => p.Uri?.Equals(uri) == true) == true;
        }

        public async Task<IEnumerable<FileSystemItem>> ListByType(string path, string extension)
        {
            var contents = await ListContents(path);
            return contents.Where(f => !f.IsDirectory && f.Name.EndsWith(extension));
        }


        public async Task<IEnumerable<FileSystemItem>> SearchFiles(string path, string searchPattern, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            return await Task.Run(() =>
            {
                var dir = new Java.IO.File(path);
                if (!dir.Exists())
                    throw new DirectoryNotFoundException($"Directory not found: {path}");

                cancellationToken.ThrowIfCancellationRequested();
                var files = dir.ListFiles(new FilenameFilter(searchPattern)) ?? [];
                return files.Select(file => new FileSystemItem
                {
                    Name = file.Name,
                    Path = file.AbsolutePath,
                    IsDirectory = file.IsDirectory,
                    Size = file.Length(),
                    ModifiedDate = DateTimeOffset.FromUnixTimeMilliseconds(file.LastModified()).DateTime
                });
            }, cancellationToken);
        }

        public Dictionary<string, string> GetFileProperties(string path)
        {
            ThrowIfDisposed();
            EnsureStoragePermissions();
            LogOperation(nameof(GetFileProperties), path);
            ValidatePathWithinRoot(path);

            var file = new Java.IO.File(path);
            if (!file.Exists())
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            var properties = new Dictionary<string, string>
            {
                ["Name"] = file.Name,
                ["Path"] = file.AbsolutePath,
                ["Permissions"] = $"Read: {file.CanRead()}, Write: {file.CanWrite()}, Execute: {file.CanExecute()}",
                ["LastModified"] = DateTimeOffset.FromUnixTimeMilliseconds(file.LastModified()).DateTime.ToString(),
                ["Size"] = file.Length().ToString(),
                ["IsHidden"] = file.IsHidden.ToString(),
                ["IsDirectory"] = file.IsDirectory.ToString()
            };

            // Add file system info if available
            if (_cachedUsage.HasValue)
            {
                properties["QuotaUsage"] = $"{_cachedUsage.Value}/{_quotaConfig.MaxSizeBytes}";
                properties["QuotaPercentage"] = $"{(_cachedUsage.Value * 100.0 / _quotaConfig.MaxSizeBytes):F1}%";
                properties["QuotaAvailable"] = $"{_quotaConfig.MaxSizeBytes - _cachedUsage.Value}";
                properties["QuotaEnforced"] = _quotaConfig.EnforceQuota.ToString();
            }

            return properties;
        }
        public async Task MigrateToNewRoot(string newRootPath, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation("MigrateToNewRoot", $"from {_rootPath} to {newRootPath}");

            try
            {
                // Create the new root if it doesn't exist
                if (!Directory.Exists(newRootPath))
                    Directory.CreateDirectory(newRootPath);

                // Migrate data
                await MigrateDataIfNeeded(_rootPath, newRootPath, cancellationToken);

                _logger.LogInformation("Migration completed from {OldPath} to {NewPath}", _rootPath, newRootPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate data to new root path: {NewPath}", newRootPath);
                throw;
            }
        }

        private async Task MigrateDataIfNeeded(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (sourcePath == destinationPath)
                return;

            if (!Directory.Exists(sourcePath))
                return;

            // Calculate total size
            var totalSize = await Task.Run(() =>
            {
                var sourceDir = new Java.IO.File(sourcePath);
                return GetDirectorySizeRecursive(sourceDir);
            }, cancellationToken);

            // Create progress reporting
            IProgress<double> progress = new Progress<double>(p =>
                _logger.LogInformation("Migration progress: {Progress:P2}", p));

            // Enumerate all items in source directory
            var sourceDir = new Java.IO.File(sourcePath);
            var sourceItems = sourceDir.ListFiles();

            if (sourceItems == null || sourceItems.Length == 0)
                return;

            int processedItems = 0;
            foreach (var item in sourceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourcePath, item.AbsolutePath);

                var destPath = Path.Combine(destinationPath, relativePath);

                if (item.IsDirectory)
                {
                    await CreateDirectory(destPath, cancellationToken);

                    // Recursively copy contents
                    await MigrateDataIfNeeded(item.AbsolutePath, destPath, cancellationToken);
                }
                else
                {
                    await  WithFileSystemErrorHandling<Task>(item.AbsolutePath, async () =>
                    {
                        var destFile = new Java.IO.File(destPath);
                        var destParent = destFile.ParentFile;
                        if (destParent != null && !destParent.Exists())
                        {
                            destParent.Mkdirs();
                        }

                        await Task.Run(() =>
                        {
                            CopyFile(item, destFile, progress, cancellationToken);
                        }, cancellationToken);
                        return Task.CompletedTask;
                    });
                }

                processedItems++;
                progress.Report((double)processedItems / sourceItems.Length);
            }

            _cachedUsage = null; // Invalidate cache after migration
        }

        private async Task<Stream> OpenDocumentUri(global::Android.Net.Uri uri, CancellationToken cancellationToken)
        {
            try
            {
                var contentResolver = _context.ContentResolver;
                ArgumentNullException.ThrowIfNull(contentResolver, "ContentResolver is not available");


                var pfd = contentResolver.OpenFileDescriptor(uri, "r");
                ArgumentNullException.ThrowIfNull(pfd, "Could not open file descriptor for URI");


                var stream = new ParcelFileDescriptorStream(pfd, "r");

                // For smaller files, load into memory for better performance
                if (pfd.StatSize <= LargeFileThreshold)
                {
                    var memoryStream = new MemoryStream((int)pfd.StatSize);
                    await stream.CopyToAsync(memoryStream, DefaultBufferSize, cancellationToken);
                    await stream.DisposeAsync();
                    memoryStream.Position = 0;
                    return memoryStream;
                }

                return stream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open document URI: {Uri}", uri);
                throw new System.IO.IOException($"Failed to open document URI", ex);
            }
        }

        // Add a method to support handling SAF URIs
        public async Task<Stream> OpenSafUri(string uriString, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LogOperation("OpenSafUri", uriString);

            var uri = global::Android.Net.Uri.Parse(uriString);

            ArgumentNullException.ThrowIfNull(uri, nameof(uriString));


            return await OpenDocumentUri(uri, cancellationToken);
        }


        // Add a method to support Android 14's Photo Picker API
        public async Task<Stream?> OpenPhotoPickerFile(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                var fileResult = await Microsoft.Maui.Storage.FilePicker.PickAsync(new Microsoft.Maui.Storage.PickOptions
                {
                    PickerTitle = "Select a photo",
                    FileTypes = Microsoft.Maui.Storage.FilePickerFileType.Images
                });

                if (fileResult != null)
                {
                    // Check if file is accessible
                    if (fileResult.FileName.Contains("://"))
                    {
                        // This is a content URI, use our OpenSafUri method
                        return await OpenSafUri(fileResult.FullPath, cancellationToken);
                    }
                    else
                    {
                        // This is a regular file path
                        return await OpenFile(fileResult.FullPath, cancellationToken);
                    }
                }

                return null; // User canceled or no selection
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening Photo Picker");
                throw new System.IO.IOException("Failed to open Photo Picker", ex);
            }
        }


        /// <summary>
        /// Provides information about Android 14 storage capabilities and permission statuses.
        /// </summary>
        /// <returns>
        /// Dictionary containing Android 14 capability flags:
        /// - IsExternalStorageManager: Whether the app has all-files access
        /// - HasPhotoPickerApi: Whether Photo Picker API is available
        /// - HasPartialMediaAccess: Whether partial media access is supported
        /// - RequiresPhotoPickerForMediaAccess: Whether Photo Picker is recommended for media access
        /// - HasVisualUserSelectedPermission: Whether READ_MEDIA_VISUAL_USER_SELECTED permission is granted
        /// </returns>
        /// <remarks>
        /// Use this method to determine the best approach for file access on Android 14 devices.
        /// </remarks>
        public Dictionary<string, bool> GetAndroid14Capabilities()
        {
            var capabilities = new Dictionary<string, bool>();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                bool isExternalStorageManager = global::Android.OS.Environment.IsExternalStorageManager;

                capabilities["IsExternalStorageManager"] = isExternalStorageManager;
                capabilities["HasPhotoPickerApi"] = true;
                capabilities["HasPartialMediaAccess"] = true; // Android 14 supports partial access
                capabilities["RequiresPhotoPickerForMediaAccess"] = true; // Android 14 prefers Photo Picker

                // Check for READ_MEDIA_VISUAL_USER_SELECTED permission
                var visualUserSelectedPermission = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                    _context, "android.permission.READ_MEDIA_VISUAL_USER_SELECTED");

                capabilities["HasVisualUserSelectedPermission"] =
                    visualUserSelectedPermission == global::Android.Content.PM.Permission.Granted;
            }
            else
            {
                capabilities["IsExternalStorageManager"] = Build.VERSION.SdkInt >= BuildVersionCodes.R &&
                    global::Android.OS.Environment.IsExternalStorageManager;
                capabilities["HasPhotoPickerApi"] = false;
                capabilities["HasPartialMediaAccess"] = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu;
                capabilities["RequiresPhotoPickerForMediaAccess"] = false;
            }

            return capabilities;
        }

    }


    #region Helper Classes

    // Move the FilenameFilter class definition here
    class FilenameFilter(string searchPattern) : Java.Lang.Object, IFilenameFilter
    {
        private readonly string _searchPattern = searchPattern;

        public bool Accept(Java.IO.File? dir, string? name)
        {
            return name != null && name.Contains(_searchPattern, StringComparison.OrdinalIgnoreCase);
        }
    }
    #endregion

}

