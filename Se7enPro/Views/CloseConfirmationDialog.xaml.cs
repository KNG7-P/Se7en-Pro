using System.Windows;

namespace Se7enPro.Views;

public enum CloseAction
{
    Cancel,
    Minimize,
    Exit,
}

public partial class CloseConfirmationDialog : Window
{
    public CloseAction Result { get; private set; } = CloseAction.Cancel;
    public bool RememberChoice => RememberCheckBox.IsChecked == true;

    public CloseConfirmationDialog()
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

    private void OnMinimizeRowClicked(object sender, RoutedEventArgs e)
    {
        Result = CloseAction.Minimize;
        DialogResult = true;
        Close();
    }

    private void OnExitRowClicked(object sender, RoutedEventArgs e)
    {
        Result = CloseAction.Exit;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Result = CloseAction.Cancel;
        DialogResult = false;
        Close();
    }
}
