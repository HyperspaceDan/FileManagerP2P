using FileManagerP2P.ViewModels;
namespace FileManagerP2P.Views;

public partial class FileDetailsPage : ContentPage
{
    public FileDetailsPage(FileDetailsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
    private void OnDragStarting(object sender, DragStartingEventArgs e)
    {
        var vm = BindingContext as FileDetailsViewModel;
#if WINDOWS
        vm?.InitializeDragDrop();
#endif
        
    }

}