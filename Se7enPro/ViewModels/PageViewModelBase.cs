using CommunityToolkit.Mvvm.ComponentModel;

namespace Se7enPro.ViewModels;

public abstract partial class PageViewModelBase : ObservableObject
{
    public abstract string Title { get; }

    public abstract string Route { get; }

    public abstract string Icon { get; }
}
