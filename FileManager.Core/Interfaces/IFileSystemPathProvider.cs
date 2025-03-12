using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManager.Core.Interfaces
{
    /// <summary>
    /// Provides secure storage and retrieval of file system root paths
    /// </summary>
    public interface IFileSystemPathProvider
    {
        /// <summary>
        /// Gets the configured root path or the default path if none is configured
        /// </summary>
        Task<string> GetRootPathAsync();

        /// <summary>
        /// Sets a custom root path to be used for file system operations
        /// </summary>
        /// <param name="path">The full path to use as root</param>
        Task SetRootPathAsync(string path);

        /// <summary>
        /// Determines if a custom path has been configured
        /// </summary>
        Task<bool> HasCustomPathAsync();

        /// <summary>
        /// Resets to the default path, removing any custom configuration
        /// </summary>
        Task ResetToDefaultPathAsync();
    }
}
