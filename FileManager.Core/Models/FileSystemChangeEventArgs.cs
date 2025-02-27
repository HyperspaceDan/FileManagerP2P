using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace FileManager.Core.Models
{
    public enum FileSystemChangeType
    {
        Created,
        Modified,
        Deleted,
        Renamed
    }
    public class FileSystemChangeEventArgs(string path, FileSystemChangeType changeType, string? newPath = null) : EventArgs
    {
        public string Path { get; } = path;
        public string? NewPath { get; } = newPath;
        public FileSystemChangeType ChangeType { get; } = changeType;
        public DateTime Timestamp { get; } = DateTime.UtcNow;
    }
    
}
