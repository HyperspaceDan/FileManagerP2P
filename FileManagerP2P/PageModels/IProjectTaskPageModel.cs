using CommunityToolkit.Mvvm.Input;
using FileManagerP2P.Models;

namespace FileManagerP2P.PageModels;

public interface IProjectTaskPageModel
{
	IAsyncRelayCommand<ProjectTask> NavigateToTaskCommand { get; }
	bool IsBusy { get; }
}