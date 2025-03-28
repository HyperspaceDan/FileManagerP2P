using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Core.Interfaces;
using FileManager.Core.Models;
using System.Diagnostics;
using Microsoft.Maui.ApplicationModel;
using CommunityToolkit.Maui.Core;
#if ANDROID
using Android.Telephony;
#endif

namespace FileManagerP2P.ViewModels
{
    public interface ITelephonyService
    {
        // Define methods you need from TelephonyManager
        string GetDeviceId();
        // Other methods you need
    }
    public partial class FileExplorerViewModel : ObservableObject
    {
        private readonly FileManager.Core.Interfaces.IFileSystem _fileSystem;
        private readonly ITelephonyService _telephonyService;
        private CancellationTokenSource? _cts;
        partial void OnSelectedFileSystemItemChanged(FileSystemItem value);

        private ObservableCollection<FileSystemItem> _fileSystemItems = [];
        public ObservableCollection<FileSystemItem> FileSystemItems
        {
            get => _fileSystemItems;
            set => SetProperty(ref _fileSystemItems, value);
        }

        private string _currentDirectoryPath = string.Empty;
        public string CurrentDirectoryPath
        {
            get => _currentDirectoryPath;
            set => SetProperty(ref _currentDirectoryPath, value);
        }

        private string _selectedFileSystemPath = string.Empty;
        public string SelectedFileSystemPath
        {
            get => _selectedFileSystemPath;
            set => SetProperty(ref _selectedFileSystemPath, value);
        }

        private FileSystemItem? _selectedFileSystemItem;
        public FileSystemItem? SelectedFileSystemItem
        {
            get => _selectedFileSystemItem;
            set => SetProperty(ref _selectedFileSystemItem, value);
        }

        private bool _isFileSystemLoading;
        public bool IsFileSystemLoading
        {
            get => _isFileSystemLoading;
            set => SetProperty(ref _isFileSystemLoading, value);
        }

        private bool _canNavigateUpFileSystem;
        public bool CanNavigateUpFileSystem
        {
            get => _canNavigateUpFileSystem;
            set => SetProperty(ref _canNavigateUpFileSystem, value);
        }

        private string _fileSystemErrorMessage = string.Empty;
        public string FileSystemErrorMessage
        {
            get => _fileSystemErrorMessage;
            set => SetProperty(ref _fileSystemErrorMessage, value);
        }

        private bool _hasFileSystemError;
        public bool HasFileSystemError
        {
            get => _hasFileSystemError;
            set => SetProperty(ref _hasFileSystemError, value);
        }

        private string _fileSystemSearchQuery = string.Empty;
        public string FileSystemSearchQuery
        {
            get => _fileSystemSearchQuery;
            set => SetProperty(ref _fileSystemSearchQuery, value);
        }

        private string _deviceIdentifier = string.Empty;
        public string DeviceIdentifier
        {
            get => _deviceIdentifier;
            set => SetProperty(ref _deviceIdentifier, value);
        }





        // Add these properties to the FileExplorerViewModel class:

        private bool _isPreviewVisible;
        public bool IsPreviewVisible
        {
            get => _isPreviewVisible;
            set => SetProperty(ref _isPreviewVisible, value);
        }

        private string _previewContent = string.Empty;
        public string PreviewContent
        {
            get => _previewContent;
            set => SetProperty(ref _previewContent, value);
        }

        private bool _isPreviewLoading;
        public bool IsPreviewLoading
        {
            get => _isPreviewLoading;
            set => SetProperty(ref _isPreviewLoading, value);
        }

        private bool _isPreviewImageVisible;
        public bool IsPreviewImageVisible
        {
            get => _isPreviewImageVisible;
            set => SetProperty(ref _isPreviewImageVisible, value);
        }

        private bool _isPreviewTextVisible;
        public bool IsPreviewTextVisible
        {
            get => _isPreviewTextVisible;
            set => SetProperty(ref _isPreviewTextVisible, value);
        }

        private bool _isPreviewUnsupportedVisible;
        public bool IsPreviewUnsupportedVisible
        {
            get => _isPreviewUnsupportedVisible;
            set => SetProperty(ref _isPreviewUnsupportedVisible, value);
        }

        private ImageSource? _previewImage;
        public ImageSource? PreviewImage
        {
            get => _previewImage;
            set => SetProperty(ref _previewImage, value);
        }

        [RelayCommand]
        private async Task PreviewSelectedFile(object parameter)
        {
            // This command can be used to show/hide the preview
            if (parameter == null)
            {
                // When null is passed, hide the preview
                IsPreviewVisible = false;
                return;
            }

            // Otherwise, show preview if there's a selected item
            await ShowPreviewSelectedFileAsync();
        }


        // Then add this method to handle file selection and preview
        [RelayCommand]
        private async Task ShowPreviewSelectedFileAsync()
        {
            if (SelectedFileSystemItem == null || SelectedFileSystemItem.IsDirectory)
            {
                IsPreviewVisible = false;
                return;
            }

            try
            {
                IsPreviewLoading = true;
                IsPreviewVisible = true;

                // Reset preview states
                IsPreviewImageVisible = false;
                IsPreviewTextVisible = false;
                IsPreviewUnsupportedVisible = false;

                string fileExtension = Path.GetExtension(SelectedFileSystemItem.Path).ToLowerInvariant();

                // Handle different file types
                if (IsImageFile(fileExtension))
                {
                    await LoadImagePreviewAsync(SelectedFileSystemItem.Path);
                }
                else if (IsTextFile(fileExtension))
                {
                    await LoadTextPreviewAsync(SelectedFileSystemItem.Path);
                }
                else
                {
                    // Unsupported file type
                    IsPreviewUnsupportedVisible = true;
                    PreviewContent = $"Preview not available for {fileExtension} files";
                }
            }
            catch (Exception ex)
            {
                IsPreviewUnsupportedVisible = true;
                PreviewContent = $"Error loading preview: {ex.Message}";
            }
            finally
            {
                IsPreviewLoading = false;
            }
        }

        private static bool IsImageFile(string extension)
        {
            return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp";
        }

        private static bool IsTextFile(string extension)
        {
            return extension is ".txt" or ".json" or ".xml" or ".html" or ".htm" or ".css" or ".js" or ".md"
                or ".cs" or ".xaml" or ".csv" or ".log";
        }

        private async Task LoadImagePreviewAsync(string path)
        {
            try
            {
                // Load image from file on a background thread
                var imageSource = await Task.Run(() => ImageSource.FromFile(path));
                PreviewImage = ImageSource.FromFile(path);
                IsPreviewImageVisible = true;
            }
            catch
            {
                IsPreviewUnsupportedVisible = true;
                PreviewContent = "Unable to load image preview";
            }
        }

        private async Task LoadTextPreviewAsync(string path)
        {
            try
            {
                // Read text file content (with limit to prevent loading very large files)
                const int maxPreviewLength = 100000; // Limit preview to 100K characters
                using var stream = await _fileSystem.OpenFile(path);
                using var reader = new StreamReader(stream);

                var content = await reader.ReadToEndAsync();
                if (content.Length > maxPreviewLength)
                {
                    content = string.Concat(content.AsSpan(0, maxPreviewLength) , "...\n\n[File too large to display completely]");
                }

                PreviewContent = content;
                IsPreviewTextVisible = true;
            }
            catch
            {
                IsPreviewUnsupportedVisible = true;
                PreviewContent = "Unable to load text preview";
            }
        }

        // Update the property observer for SelectedFileSystemItem
        partial void OnSelectedFileSystemItemChanged(FileSystemItem value)
        {
            if (value != null)
            {
                // Call the method directly instead of using the command
                // Use MainThread to safely invoke the async method without directly awaiting it
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await ShowPreviewSelectedFileAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error previewing file: {ex.Message}");
                        // Optionally handle the exception here
                    }
                });
            }
            else
            {
                IsPreviewVisible = false;
            }
        }


        public FileExplorerViewModel(FileManager.Core.Interfaces.IFileSystem fileSystem, ITelephonyService telephonyService)
        {
            _fileSystem = fileSystem;
            _telephonyService = telephonyService;

            // Subscribe to file system changes if they occur
            if (_fileSystem is INotifyFileSystemChanged notifier)
            {
                notifier.FileSystemChanged += OnFileSystemChanged;
            }
            InitializeDeviceIdentifier();
        }
        private void InitializeDeviceIdentifier()
        {
            try
            {
                _deviceIdentifier = _telephonyService.GetDeviceId();
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to get device identifier: {ex.Message}");
                _deviceIdentifier = "Unknown Device";
            }
        }

        private void OnFileSystemChanged(object? sender, FileSystemChangeEventArgs e)
        {
            // Refresh the current directory when file system changes are detected
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await RefreshFileSystemAsync();
            });
        }


        [RelayCommand]
        private async Task NavigateToFileSystemAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                IsFileSystemLoading = true;
                HasFileSystemError = false;
                FileSystemErrorMessage = string.Empty;

                // Cancel any ongoing operations
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                // Update and load the new path
                CurrentDirectoryPath = path;
                await LoadFileSystemItemsAsync(_cts.Token);

                // Update navigation state
                CanNavigateUpFileSystem = !string.IsNullOrEmpty(CurrentDirectoryPath);
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, ignore
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to navigate: {ex.Message}");
            }
            finally
            {
                IsFileSystemLoading = false;
            }
        }

        [RelayCommand]
        private async Task NavigateUpFileSystemAsync()
        {
            if (string.IsNullOrEmpty(CurrentDirectoryPath))
                return;

            try
            {
                var parent = Path.GetDirectoryName(CurrentDirectoryPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    await NavigateToFileSystemAsync(parent);
                }
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to navigate up: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task RefreshFileSystemAsync()
        {
            try
            {
                IsFileSystemLoading = true;
                HasFileSystemError = false;
                FileSystemErrorMessage = string.Empty;

                // Cancel any ongoing operations
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                await LoadFileSystemItemsAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, ignore
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to refresh: {ex.Message}");
            }
            finally
            {
                IsFileSystemLoading = false;
            }
        }

        [RelayCommand]
        private async Task OpenSelectedFileSystemItemAsync()
        {
            if (SelectedFileSystemItem == null)
                return;

            try
            {
                if (SelectedFileSystemItem.IsDirectory)
                {
                    await NavigateToFileSystemAsync(SelectedFileSystemItem.Path);
                }
                else
                {
                    // Here you can implement file opening behavior
                    // For example, you might want to open a file preview or download it
                    SelectedFileSystemPath = SelectedFileSystemItem.Path;
                    // Raise an event or call a method to handle file opening
                }
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to open item: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task SearchFileSystemAsync()
        {
            if (string.IsNullOrWhiteSpace(FileSystemSearchQuery))
            {
                await RefreshFileSystemAsync();
                return;
            }

            try
            {
                IsFileSystemLoading = true;
                HasFileSystemError = false;
                FileSystemErrorMessage = string.Empty;

                // Cancel any ongoing operations
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                // Load all items and then filter
                var allItems = await _fileSystem.ListContents(CurrentDirectoryPath, _cts.Token);
                var filteredItems = allItems.Where(item =>
                    item.Name.Contains(FileSystemSearchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

                FileSystemItems = [.. filteredItems];
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, ignore
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Search failed: {ex.Message}");
            }
            finally
            {
                IsFileSystemLoading = false;
            }
        }

        [RelayCommand]
        private async Task CreateFileSystemFolderAsync(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return;

            try
            {
                IsFileSystemLoading = true;
                HasFileSystemError = false;
                FileSystemErrorMessage = string.Empty;

                string newFolderPath = Path.Combine(CurrentDirectoryPath, folderName);
                await _fileSystem.CreateDirectory(newFolderPath);

                // Refresh to show the new folder
                await RefreshFileSystemAsync();
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to create folder: {ex.Message}");
            }
            finally
            {
                IsFileSystemLoading = false;
            }
        }

        [RelayCommand]
        private async Task DeleteSelectedFileSystemItemAsync()
        {
            if (SelectedFileSystemItem == null)
                return;

            try
            {
                IsFileSystemLoading = true;
                HasFileSystemError = false;
                FileSystemErrorMessage = string.Empty;

                await _fileSystem.DeleteItem(SelectedFileSystemItem.Path);

                // Refresh to update the view
                await RefreshFileSystemAsync();
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to delete item: {ex.Message}");
            }
            finally
            {
                IsFileSystemLoading = false;
            }
        }

        private async Task LoadFileSystemItemsAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(CurrentDirectoryPath))
                return;

            try
            {
                var items = await _fileSystem.ListContents(CurrentDirectoryPath, cancellationToken);

                // Sort: directories first, then files alphabetically
                var sortedItems = items
                    .OrderByDescending(i => i.IsDirectory)
                    .ThenBy(i => i.Name)
                    .ToList();

                FileSystemItems = [.. sortedItems];
            }
            catch (Exception ex)
            {
                HandleFileSystemError($"Failed to load items: {ex.Message}");
                FileSystemItems.Clear();
            }
        }

        private void HandleFileSystemError(string message)
        {
            FileSystemErrorMessage = message;
            HasFileSystemError = true;
            Debug.WriteLine($"FileExplorer Error: {message}");
        }

        // Cleanup when the view model is no longer needed
        public void Cleanup()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // Unsubscribe from events
            if (_fileSystem is INotifyFileSystemChanged notifier)
            {
                notifier.FileSystemChanged -= OnFileSystemChanged;
            }
        }
    }

    // Define this interface if it doesn't already exist
    public interface INotifyFileSystemChanged
    {
        event EventHandler<FileSystemChangeEventArgs> FileSystemChanged;
    }
}
