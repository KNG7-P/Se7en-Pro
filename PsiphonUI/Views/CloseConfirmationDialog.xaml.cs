using System.Windows;
using System.Windows.Input;

namespace PsiphonUI.Views;

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
    }

    private void OnMinimizeRowClicked(object sender, MouseButtonEventArgs e)
    {
        Result = CloseAction.Minimize;
        DialogResult = true;
        Close();
    }

    private void OnExitRowClicked(object sender, MouseButtonEventArgs e)
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
