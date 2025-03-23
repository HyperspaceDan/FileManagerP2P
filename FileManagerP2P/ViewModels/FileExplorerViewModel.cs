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
