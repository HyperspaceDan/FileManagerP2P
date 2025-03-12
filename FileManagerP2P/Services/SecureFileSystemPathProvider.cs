using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using FileManager.Core.Interfaces;
using Microsoft.Maui.Storage;

namespace FileManagerP2P.Services
{
    public class SecureFileSystemPathProvider : IFileSystemPathProvider
    {
        private const string RootPathKey = "FileSystem_RootPath";
        private readonly string _defaultPath;

        public SecureFileSystemPathProvider(string defaultPath)
        {
            _defaultPath = defaultPath ?? throw new ArgumentNullException(nameof(defaultPath));
        }

        public async Task<string> GetRootPathAsync()
        {
            var storedPath = await SecureStorage.Default.GetAsync(RootPathKey);
            return !string.IsNullOrEmpty(storedPath) ? storedPath : _defaultPath;
        }

        public Task SetRootPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty", nameof(path));

            if (!Directory.Exists(path) && !TryCreateDirectory(path))
                throw new DirectoryNotFoundException($"Directory does not exist and could not be created: {path}");

            return SecureStorage.Default.SetAsync(RootPathKey, path);
        }

        public async Task<bool> HasCustomPathAsync()
        {
            var path = await SecureStorage.Default.GetAsync(RootPathKey);
            return !string.IsNullOrEmpty(path);
        }

        public Task ResetToDefaultPathAsync()
        {
            SecureStorage.Default.Remove(RootPathKey);
            return Task.CompletedTask;
        }

        private static bool TryCreateDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }
    }
}
