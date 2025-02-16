using FileManagerP2P.Models;
using FileManagerP2P.PageModels;

namespace FileManagerP2P.Pages;

public partial class MainPage : ContentPage
{
	public MainPage(MainPageModel model)
	{
		InitializeComponent();
		BindingContext = model;
	}
}