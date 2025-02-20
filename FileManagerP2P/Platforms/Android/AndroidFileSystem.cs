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
using FileManager.Core.Models;
using Android.Content;
using Android.App.Usage;

namespace FileManagerP2P.Platforms.Android
{
    public class AndroidFileSystem : FileManager.Core.Interfaces.IFileSystem
    {
        private readonly Context _context;

        public AndroidFileSystem()
        {
            _context = global::Android.App.Application.Context
                ?? throw new InvalidOperationException("Application context is not available");
        }

        private void EnsureStoragePermissions()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R) //Android 11 (API 30) and above
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    if (!global::Android.OS.Environment.IsExternalStorageManager)
                    {
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
                    throw new UnauthorizedAccessException(
                        "App requires READ_EXTERNAL_STORAGE and WRITE_EXTERNAL_STORAGE permissions");
                }
            }
            else // Android 5.x (API 21-22)
            {
                // Before Android 6.0, permissions were granted at install time
                // Just verify if the permissions are declared in the manifest
                var packageInfo = _context.PackageManager!.GetPackageInfo(_context.PackageName!, PackageInfoFlags.Permissions);
                var declaredPermissions = packageInfo?.RequestedPermissions;

                if (declaredPermissions == null ||
                    !declaredPermissions.Contains(global::Android.Manifest.Permission.ReadExternalStorage) ||
                    !declaredPermissions.Contains(global::Android.Manifest.Permission.WriteExternalStorage))
                {
                    throw new UnauthorizedAccessException(
                        "Storage permissions are not declared in the manifest");
                }
            }
        }

        public async Task<IEnumerable<FileSystemItem>> ListContents(string path, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = new Java.IO.File(path);
                if (!directory.Exists())
                    throw new DirectoryNotFoundException($"Directory not found: {path}");
                var files = directory.ListFiles();
                if (files == null) return Enumerable.Empty<FileSystemItem>();

                var fileSystemItems = new List<FileSystemItem>(files.Length);
                var parallelOptions = new ParallelOptions
                {
                    CancellationToken = cancellationToken
                };
                Parallel.ForEach(files, parallelOptions, file =>
                {
                    var fileSystemItem = new FileSystemItem
                    {
                        Name = file.Name,
                        Path = file.AbsolutePath,
                        IsDirectory = file.IsDirectory,
                        Size = file.Length(),
                        ModifiedDate = DateTimeOffset.FromUnixTimeMilliseconds(file.LastModified()).DateTime
                    };
                    lock (fileSystemItems)
                    {
                        fileSystemItems.Add(fileSystemItem);
                    }
                });

                return fileSystemItems;
            }, cancellationToken);
        }

        public async Task<Stream> OpenFile(string path)
        {
            EnsureStoragePermissions();
            var file = new Java.IO.File(path);
            if (!file.Exists())
                throw new Java.IO.FileNotFoundException($"File not found: {path}");
            return await Task.FromResult<Stream>(System.IO.File.OpenRead(path));
        }

        public async Task WriteFile(string path, Stream content, int bufferSize = 81920, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            using var fileStream = new FileStream(path, FileMode.Create);
            await content.CopyToAsync(fileStream, bufferSize, cancellationToken);
        }

        public async Task CreateDirectory(string path)
        {
            EnsureStoragePermissions();
            var result = new Java.IO.File(path).Mkdirs();
            if (!result)
                throw new Java.IO.IOException($"Failed to create directory at {path}");
            await Task.CompletedTask;
        }

        public async Task DeleteItem(string path, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            await Task.Run(() =>
            {
                var file = new Java.IO.File(path);
                if (!file.Exists())
                    throw new Java.IO.FileNotFoundException($"Path not found: {path}");

                if (file.IsDirectory)
                    DeleteRecursive(file, cancellationToken);
                else if (!file.Delete())
                    throw new Java.IO.IOException($"Failed to delete file: {path}");
            }, cancellationToken);
        }

        public async Task CopyItem(string sourcePath, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            EnsureStoragePermissions();
            await Task.Run(() =>
            {
                var source = new Java.IO.File(sourcePath);
                var dest = new Java.IO.File(destinationPath);

                if (!source.Exists())
                    throw new Java.IO.FileNotFoundException($"Source path not found: {sourcePath}");

                if (source.IsFile)
                    CopyFile(source, dest, progress, cancellationToken);
                else
                    CopyDirectory(source, dest, progress, cancellationToken);
            }, cancellationToken);
        }

        public async Task RenameItem(string oldPath, string newPath)
        {
            EnsureStoragePermissions();
            var oldFile = new Java.IO.File(oldPath);
            if (!oldFile.Exists())
                throw new Java.IO.FileNotFoundException($"File not found: {oldPath}");

            if (!oldFile.RenameTo(new Java.IO.File(newPath)))
                throw new Java.IO.IOException($"Failed to rename {oldPath} to {newPath}");
            await Task.CompletedTask;
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
            EnsureStoragePermissions();
            var file = new Java.IO.File(path);
            if (!file.Exists())
                throw new Java.IO.FileNotFoundException($"File not found: {path}");

            return new Dictionary<string, string>
            {
                ["Permissions"] = $"Read: {file.CanRead()}, Write: {file.CanWrite()}",
                ["LastModified"] = DateTimeOffset.FromUnixTimeMilliseconds(file.LastModified()).DateTime.ToString(),
                ["Size"] = file.Length().ToString(),
                ["IsHidden"] = file.IsHidden.ToString()
            };
        }



    }

    class FilenameFilter(string searchPattern) : Java.Lang.Object, IFilenameFilter
    {
        private readonly string _searchPattern = searchPattern;

        public bool Accept(Java.IO.File? dir, string? name)
        {
            return name != null && name.Contains(_searchPattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
