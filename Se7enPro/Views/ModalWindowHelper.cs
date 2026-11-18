using System;
using System.Windows;

namespace Se7enPro.Views;

public static class ModalWindowHelper
{
    public static (double Left, double Top) ComputeCenteredPosition(
        double ownerLeft,
        double ownerTop,
        double ownerWidth,
        double ownerHeight,
        bool hasValidOwner,
        double dialogWidth,
        double dialogHeight,
        Rect workArea)
    {
        double targetLeft;
        double targetTop;

        if (hasValidOwner && ownerWidth > 0 && ownerHeight > 0)
        {
            targetLeft = ownerLeft + (ownerWidth - dialogWidth) / 2.0;
            targetTop = ownerTop + (ownerHeight - dialogHeight) / 2.0;
        }
        else
        {
            targetLeft = workArea.Left + (workArea.Width - dialogWidth) / 2.0;
            targetTop = workArea.Top + (workArea.Height - dialogHeight) / 2.0;
        }

        var maxLeft = Math.Max(workArea.Left, workArea.Right - dialogWidth);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - dialogHeight);

        var clampedLeft = Math.Min(Math.Max(targetLeft, workArea.Left), maxLeft);
        var clampedTop = Math.Min(Math.Max(targetTop, workArea.Top), maxTop);

        return (Math.Round(clampedLeft), Math.Round(clampedTop));
    }

    public static (double MaxWidth, double MaxHeight) ComputeMaxDimensions(
        double ownerHeight,
        bool hasValidOwner,
        Rect workArea)
    {
        var screenMaxW = Math.Max(320, workArea.Width - 24);
        var screenMaxH = Math.Max(320, workArea.Height - 24);

        if (hasValidOwner && ownerHeight > 200)
        {
            var boundedByOwner = Math.Max(Math.Min(ownerHeight, screenMaxH), Math.Min(560, screenMaxH));
            screenMaxH = Math.Min(screenMaxH, boundedByOwner);
        }

        return (screenMaxW, screenMaxH);
    }

    public static void AttachCentered(Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        var hasCenteredInitially = false;

        void UpdateBoundsAndPosition(bool forceCenter)
        {
            var workArea = SystemParameters.WorkArea;
            var owner = dialog.Owner;
            var hasValidOwner = owner is { IsLoaded: true } && owner.WindowState != WindowState.Minimized;

            double ownerLeft = 0, ownerTop = 0, ownerW = 0, ownerH = 0;
            if (hasValidOwner && owner is not null)
            {
                if (owner.WindowState == WindowState.Maximized)
                {
                    ownerLeft = workArea.Left;
                    ownerTop = workArea.Top;
                    ownerW = workArea.Width;
                    ownerH = workArea.Height;
                }
                else
                {
                    ownerLeft = owner.Left;
                    ownerTop = owner.Top;
                    ownerW = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
                    ownerH = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
                }
            }

            var (maxW, maxH) = ComputeMaxDimensions(ownerH, hasValidOwner, workArea);
            dialog.MaxWidth = maxW;
            dialog.MaxHeight = maxH;

            var dlgW = dialog.ActualWidth > 0 ? dialog.ActualWidth : (double.IsNaN(dialog.Width) ? 460 : dialog.Width);
            var dlgH = dialog.ActualHeight > 0 ? dialog.ActualHeight : (double.IsNaN(dialog.Height) ? 400 : dialog.Height);

            if (!forceCenter && hasCenteredInitially)
            {
                var (_, top) = ComputeCenteredPosition(ownerLeft, ownerTop, ownerW, ownerH, hasValidOwner, dlgW, dlgH, workArea);
                dialog.Top = top;
                var (left, _) = ComputeCenteredPosition(ownerLeft, ownerTop, ownerW, ownerH, hasValidOwner, dlgW, dlgH, workArea);
                if (dialog.Left < workArea.Left || dialog.Left + dlgW > workArea.Right)
                    dialog.Left = left;
                return;
            }

            var (cLeft, cTop) = ComputeCenteredPosition(ownerLeft, ownerTop, ownerW, ownerH, hasValidOwner, dlgW, dlgH, workArea);
            dialog.Left = cLeft;
            dialog.Top = cTop;
            hasCenteredInitially = true;
        }

        dialog.SourceInitialized += (_, _) => UpdateBoundsAndPosition(forceCenter: true);
        dialog.Loaded += (_, _) => UpdateBoundsAndPosition(forceCenter: true);
        dialog.SizeChanged += (_, _) => UpdateBoundsAndPosition(forceCenter: false);
    }
}
