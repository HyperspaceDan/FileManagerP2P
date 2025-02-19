// Platforms/Android/AndroidFileSystem.cs
using Java.IO;
using Android.OS;
using Android.App;
using Android.Net;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FileManager.Core.Interfaces;
using FileManager.Core.Models;

namespace FileManagerP2P.Platforms.Android
{
    public class AndroidFileSystem : FileManager.Core.Interfaces.IFileSystem
    {
        public async Task<IEnumerable<FileSystemItem>> ListContents(string path)
        {
            return await Task.Run(() =>
            {
                var directory = new Java.IO.File(path);
                var files = directory.ListFiles();
                if (files == null) return Enumerable.Empty<FileSystemItem>();

                var fileSystemItems = new List<FileSystemItem>(files.Length);
                Parallel.ForEach(files, file =>
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
            });
        }

        public Task<Stream> OpenFile(string path) =>
            Task.FromResult<Stream>(System.IO.File.OpenRead(path));

        public async Task WriteFile(string path, Stream content)
        {
            using var fileStream = new FileStream(path, FileMode.Create);
            await content.CopyToAsync(fileStream);
        }

        public Task CreateDirectory(string path)
        {
            new Java.IO.File(path).Mkdirs();
            return Task.CompletedTask;
        }

        public Task DeleteItem(string path)
        {
            var file = new Java.IO.File(path);
            if (file.IsDirectory) DeleteRecursive(file);
            else file.Delete();
            return Task.CompletedTask;
        }

        public Task CopyItem(string sourcePath, string destinationPath)
        {
            var source = new Java.IO.File(sourcePath);
            var dest = new Java.IO.File(destinationPath);

            if (source.IsFile) CopyFile(source, dest);
            else CopyDirectory(source, dest);
            return Task.CompletedTask;
        }

        public Task RenameItem(string oldPath, string newPath)
        {
            new Java.IO.File(oldPath).RenameTo(new Java.IO.File(newPath));
            return Task.CompletedTask;
        }

        private static void DeleteRecursive(Java.IO.File file)
        {
            if (file.IsDirectory)
            {
                var children = file.ListFiles();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        DeleteRecursive(child);
                    }
                }
            }
            file.Delete();
        }

        private static void CopyFile(Java.IO.File source, Java.IO.File dest)
        {
            using var inStream = new FileInputStream(source);
            using var outStream = new FileOutputStream(dest);
            var inChannel = inStream.Channel;
            var outChannel = outStream.Channel;

            if (inChannel != null && outChannel != null)
            {
                inChannel.TransferTo(0, inChannel.Size(), outChannel);
            }
            else
            {
                throw new InvalidOperationException("File channels cannot be null");
            }
        }

        private static void CopyDirectory(Java.IO.File source, Java.IO.File dest)
        {
            dest.Mkdirs();
            var files = source.ListFiles();
            if (files != null)
            {
                foreach (var file in files)
                {
                    var newFile = new Java.IO.File(dest, file.Name);
                    if (file.IsDirectory) CopyDirectory(file, newFile);
                    else CopyFile(file, newFile);
                }
            }
        }

        // In AndroidFileSystem.cs
        public static string GetExternalStoragePath()
        {
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

        public IEnumerable<FileSystemItem> ListByType(string path, string extension)
        {
            return ListContents(path).Result
                .Where(f => !f.IsDirectory && f.Name.EndsWith(extension));
        }


        public static IEnumerable<FileSystemItem> SearchFiles(string path, string searchPattern)
        {
            var dir = new Java.IO.File(path);
            var files = dir.ListFiles(new FilenameFilter(searchPattern)) ?? [];
            return from file in files
                   select new FileSystemItem
                   {
                       Name = file.Name,
                       Path = file.AbsolutePath,
                       IsDirectory = file.IsDirectory,
                       Size = file.Length(),
                       ModifiedDate = DateTimeOffset.FromUnixTimeMilliseconds(file.LastModified()).DateTime
                   };
        }

        public static Dictionary<string, string> GetFileProperties(string path)
        {
            var file = new Java.IO.File(path);
            return new Dictionary<string, string>
            {
                ["Permissions"] = file.CanRead() + "/" + file.CanWrite()
            };
        }



    }

    class FilenameFilter(string searchPattern) : Java.Lang.Object, IFilenameFilter
    {
        private readonly string _searchPattern = searchPattern;

        public bool Accept(Java.IO.File? dir, string? name)
        {
            return name != null && name.Contains(_searchPattern);
        }
    }
}
