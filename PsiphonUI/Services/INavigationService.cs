using System;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Services;

public interface INavigationService
{
    PageViewModelBase? Current { get; }
    event EventHandler<PageViewModelBase>? Navigated;

    void NavigateTo(string route);
}
