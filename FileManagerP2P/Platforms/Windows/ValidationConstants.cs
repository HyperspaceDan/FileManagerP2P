namespace FileManagerP2P.Platforms.Windows
{
    internal static class ValidationConstants
    {
        public const int MinBufferSize = 4096;            // 4KB minimum
        public const int MaxBufferSize = 16 * 1024 * 1024; // 16MB maximum
        public const int MaxPathLength = 260;              // Windows MAX_PATH
        public const double MinProgressInterval = 0.01;    // 1% minimum progress update
        public const long MaxFileSize = 2L * 1024 * 1024 * 1024; // 2GB maximum file size
    }
}