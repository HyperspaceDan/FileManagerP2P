using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileManager.Core.Models
{
    public class FileSystemItem 
    {
        public required string Name { get; set; }
        public required string Path { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedDate { get; set; }
    }
}
