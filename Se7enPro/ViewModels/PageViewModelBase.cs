using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public abstract partial class PageViewModelBase : ObservableObject, IDisposable
{
    private bool _localeSubscribed;

    protected PageViewModelBase()
    {
        
        
        
        Loc.Changed += OnLocaleChanged;
        _localeSubscribed = true;
    }

        public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_localeSubscribed) return;
        _localeSubscribed = false;
        Loc.Changed -= OnLocaleChanged;
    }

        private void OnLocaleChanged() => OnPropertyChanged(new PropertyChangedEventArgs(string.Empty));

        public abstract string Title { get; }

    public abstract string Route { get; }

    public abstract string Icon { get; }
}
