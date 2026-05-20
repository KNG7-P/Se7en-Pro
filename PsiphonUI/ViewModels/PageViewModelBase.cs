using CommunityToolkit.Mvvm.ComponentModel;

namespace PsiphonUI.ViewModels;

public abstract partial class PageViewModelBase : ObservableObject
{
    public abstract string Title { get; }

    public abstract string Route { get; }

    public abstract string Icon { get; }
}
