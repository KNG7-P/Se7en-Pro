using System.Windows;

namespace Se7enPro.Views;

public partial class FoxyAccountRequiredDialog : Window
{
    public FoxyAccountRequiredDialog()
    {
        InitializeComponent();
        ModalWindowHelper.AttachCentered(this);
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

    public static bool Show(Window? owner = null)
    {
        var targetOwner = owner ?? Application.Current?.MainWindow;
        var dialog = new FoxyAccountRequiredDialog();
        if (targetOwner is { IsLoaded: true } && !ReferenceEquals(targetOwner, dialog))
        {
            dialog.Owner = targetOwner;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnGoToSettingsClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDialogBorderMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }
}
