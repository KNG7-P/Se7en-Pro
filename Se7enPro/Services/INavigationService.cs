using System;
using Se7enPro.ViewModels;

namespace Se7enPro.Services;

public interface INavigationService
{
    PageViewModelBase? Current { get; }
    event EventHandler<PageViewModelBase>? Navigated;

    void NavigateTo(string route);
}
