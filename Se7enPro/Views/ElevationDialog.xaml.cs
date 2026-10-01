using System.Windows;

namespace Se7enPro.Views;

public partial class ElevationDialog : Window
{
    public ElevationDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Owner is MainWindow mw) mw.ShowBackdrop();
            else if (Application.Current?.MainWindow is MainWindow main) main.ShowBackdrop();
        };
        Closed += (_, _) =>
        {
            if (Owner is MainWindow mw) mw.HideBackdrop();
            else if (Application.Current?.MainWindow is MainWindow main) main.HideBackdrop();
        };
    }

        public static bool Confirm(Window? owner)
    {
        var dialog = new ElevationDialog();
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnElevateClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
