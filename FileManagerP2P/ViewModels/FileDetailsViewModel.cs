using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Core.Models;
using System.Windows.Input;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Controls;
using System.IO;
#if WINDOWS
using Windows.Storage;
using Windows.UI.StartScreen;
#endif

namespace FileManagerP2P.ViewModels;

public partial class FileDetailsViewModel : ObservableObject
{
    private FileSystemItem _fileItem = null!;
    public FileSystemItem FileItem
    {
        get => _fileItem;
        set => SetProperty(ref _fileItem, value);
    }

    private string _fileName = string.Empty;
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    private string _fileSize = string.Empty;
    public string FileSize
    {
        get => _fileSize;
        set => SetProperty(ref _fileSize, value);
    }

    private DateTime _lastModified;
    public DateTime LastModified
    {
        get => _lastModified;
        set => SetProperty(ref _lastModified, value);
    }

    private ImageSource _fileIcon = ImageSource.FromFile("file.png");
    public ImageSource FileIcon
    {
        get => _fileIcon;
        set => SetProperty(ref _fileIcon, value);
    }

    private ImageSource? _previewImage ;
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    private string _previewContent = string.Empty;
    public string PreviewContent
    {
        get => _previewContent;
        set => SetProperty(ref _previewContent, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private bool _isImagePreview;
    public bool IsImagePreview
    {
        get => _isImagePreview;
        set => SetProperty(ref _isImagePreview, value);
    }

    private bool _isTextPreview;
    public bool IsTextPreview
    {
        get => _isTextPreview;
        set => SetProperty(ref _isTextPreview, value);
    }

    private bool _isUnsupportedFormat;
    public bool IsUnsupportedFormat
    {
        get => _isUnsupportedFormat;
        set => SetProperty(ref _isUnsupportedFormat, value);
    }

    public FileDetailsViewModel(FileSystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        FileItem = item;
        InitializeFileDetails();
        // Start the preview loading but don't block constructor
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await LoadPreviewAsync();
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlert("Error",
                    "Failed to load preview: " + ex.Message, "OK");
            }
        });
    }

    private void InitializeFileDetails()
    {
        FileName = FileItem.Name;
        FilePath = FileItem.Path;
        FileSize = $"{FileItem.Size:N0} bytes";
        LastModified = FileItem.ModifiedDate;
        FileIcon = FileItem.IsDirectory ? "folder.png" : "file.png";
    }

    private async Task LoadPreviewAsync()
    {
        IsLoading = true;

        try
        {
            string extension = Path.GetExtension(FileName).ToLowerInvariant();

            if (IsImageFile(extension))
            {
                await LoadImagePreviewAsync();
            }
            else if (IsTextFile(extension))
            {
                await LoadTextPreviewAsync();
            }
            else
            {
                IsUnsupportedFormat = true;
            }
        }
        catch (Exception)
        {
            IsUnsupportedFormat = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadImagePreviewAsync()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            PreviewImage = ImageSource.FromStream(() => new MemoryStream(File.ReadAllBytes(FilePath)));
            IsImagePreview = true;
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", "Could not load image preview: " + ex.Message, "OK");
            IsUnsupportedFormat = true;
        }
    }

    private async Task LoadTextPreviewAsync()
    {
        try
        {
            // For large files, read async and limit preview size
            using var reader = new StreamReader(FilePath);
            const int MaxPreviewLength = 100000; // Limit preview to ~100KB
            var buffer = new char[MaxPreviewLength];

            int readCount = await reader.ReadBlockAsync(buffer, 0, MaxPreviewLength);
            PreviewContent = new string(buffer, 0, readCount);

            if (readCount == MaxPreviewLength && !reader.EndOfStream)
            {
                PreviewContent += "\n[File preview truncated...]";
            }

            IsTextPreview = true;
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", "Could not load text preview: " + ex.Message, "OK");
            IsUnsupportedFormat = true;
        }
    }


    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp"];
    private static bool IsImageFile(string extension) => ImageExtensions.Contains(extension);


    private static readonly string[] TextExtensions = [".txt", ".css", ".html", ".xml", ".json", ".md"];
    private static bool IsTextFile(string extension) => TextExtensions.Contains(extension);


    [RelayCommand]
    private async Task OpenFileAsync()
    {
        try
        {
            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(FilePath)
            });
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", "Could not open file: " + ex.Message, "OK");
        }
    }

    [RelayCommand]
    private async Task ShareFileAsync()
    {
        try
        {
            await Share.RequestAsync(new ShareFileRequest
            {
                Title = FileName,
                File = new ShareFile(FilePath)
            });
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", "Could not share file: " + ex.Message, "OK");
        }
    }

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        bool confirm = await Shell.Current.DisplayAlert(
            "Confirm Delete",
            $"Are you sure you want to delete {FileName}?",
            "Yes",
            "No");

        if (confirm)
        {
            try
            {
                File.Delete(FilePath);
                await Shell.Current.GoToAsync("..");
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlert("Error", "Could not delete file: " + ex.Message, "OK");
            }
        }
    }
}

