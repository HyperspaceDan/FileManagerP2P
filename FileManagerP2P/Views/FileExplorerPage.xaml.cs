using FileManagerP2P.ViewModels;
namespace FileManagerP2P.Views;

public partial class FileExplorerPage : ContentPage
{
    private readonly FileExplorerViewModel _viewModel;
    private readonly Grid? _rootGrid;

    public FileExplorerPage(FileExplorerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;

        // Find the root grid after InitializeComponent
        _rootGrid = Content as Grid;
        if (_rootGrid == null)
        {
            throw new InvalidOperationException("Root element must be a Grid");
        }

        // Set the default column width ratio (can adjust based on your preference)
        UpdateColumnWidths(isPreviewVisible: false);

        // Subscribe to the preview visibility change
        _viewModel.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(FileExplorerViewModel.IsPreviewVisible))
            {
                UpdateColumnWidths(_viewModel.IsPreviewVisible);
            }
        };
    }

    private void UpdateColumnWidths(bool isPreviewVisible)
    {
        if (_rootGrid == null) return;
        if (isPreviewVisible)
        {
            // When preview is visible, split the view evenly
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _rootGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            // When preview is hidden, expand the file explorer to take full width
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _rootGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }
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