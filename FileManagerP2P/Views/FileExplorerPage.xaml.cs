using FileManagerP2P.ViewModels;
namespace FileManagerP2P.Views;

public partial class FileExplorerPage : ContentPage
{
    private readonly FileExplorerViewModel _viewModel;

    public FileExplorerPage(FileExplorerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Initialize file explorer when the page appears if it hasn't been initialized yet
        if (string.IsNullOrEmpty(_viewModel.CurrentDirectoryPath))
        {
            string initialPath = FileSystem.AppDataDirectory;
            _viewModel.NavigateToFileSystemCommand.Execute(initialPath);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Optional: clean up resources if page is removed from navigation
    }
}