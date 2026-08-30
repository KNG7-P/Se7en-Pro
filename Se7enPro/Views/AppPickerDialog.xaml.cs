using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Se7enPro.Services;

namespace Se7enPro.Views;

public partial class AppPickerDialog : Window
{
    private readonly ObservableCollection<AppPickItem> _items = new();
    private readonly CancellationTokenSource _cts = new();
    private ICollectionView? _view;

    public AppPickerDialog()
    {
        InitializeComponent();
        AppList.ItemsSource = _items;

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;

        Loaded += OnLoaded;
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } _cts.Dispose(); };
    }

    public IReadOnlyList<string> SelectedValues { get; private set; } = Array.Empty<string>();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        List<InstalledAppInfo> apps;
        try
        {
            apps = await InstalledAppsProvider.LoadAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            apps = new List<InstalledAppInfo>();
        }

        if (_cts.IsCancellationRequested) return;

        foreach (var app in apps)
        {
            var item = new AppPickItem(app);
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }

        BusyIcon.Visibility = Visibility.Collapsed;
        SubtitleText.Text = _items.Count == 0
            ? "No applications found — use Browse… to pick an .exe."
            : $"{_items.Count} applications found. Tick the ones you want, then press Add.";

        SearchBox.Focus();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppPickItem.IsSelected)) return;
        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        var count = _items.Count(i => i.IsSelected);
        AddButton.IsEnabled = count > 0;
        SelectionText.Text = count switch
        {
            0 => "Nothing selected",
            1 => "1 application selected",
            _ => $"{count} applications selected",
        };
    }

    private bool FilterItem(object obj)
    {
        var needle = (SearchBox?.Text ?? "").Trim();
        if (needle.Length == 0) return true;
        return obj is AppPickItem item
            && item.SearchText.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        try { _view?.Refresh(); } catch { }
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an application",
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        SelectedValues = dialog.FileNames
            .Select(p => ToRuleValue(p, MatchByPathCheckBox.IsChecked == true))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DialogResult = SelectedValues.Count > 0;
        Close();
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var byPath = MatchByPathCheckBox.IsChecked == true;

        SelectedValues = _items
            .Where(i => i.IsSelected)
            .Select(i => ToRuleValue(i.ExePath, byPath))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DialogResult = SelectedValues.Count > 0;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        SelectedValues = Array.Empty<string>();
        DialogResult = false;
        Close();
    }

    private static string ToRuleValue(string exePath, bool byPath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "";
        exePath = exePath.Trim();
        if (byPath) return exePath;

        try { return Path.GetFileName(exePath); }
        catch { return exePath; }
    }
}

public sealed partial class AppPickItem : ObservableObject
{
    public AppPickItem(InstalledAppInfo info)
    {
        Name = info.Name;
        ExePath = info.ExePath;
        FileName = info.FileName;
        IsRunning = info.IsRunning;
        Icon = info.Icon;
        SearchText = info.SearchText;
    }

    public string Name { get; }
    public string ExePath { get; }
    public string FileName { get; }
    public bool IsRunning { get; }
    public ImageSource? Icon { get; }
    public string SearchText { get; }

    [ObservableProperty] private bool _isSelected;
}
