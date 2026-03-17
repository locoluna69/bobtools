using StarCitizenOverlayStarter.Interop;
using StarCitizenOverlayStarter.Models;
using StarCitizenOverlayStarter.Utils;
using System;
using System.Windows;
using System.Windows.Interop;

namespace StarCitizenOverlayStarter.Views;

public partial class CaptureOverlayWindow : Window
{
    private CaptureRegion _region = new();

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += CaptureOverlayWindow_SourceInitialized;
    }

    public void SetRegion(CaptureRegion region)
    {
        _region = region;
        RefreshPosition();
    }

    public void RefreshPosition()
    {
        if (!_region.IsValid || !StarCitizenWindowLocator.TryGetClientBounds(out var clientBounds, out _))
        {
            Hide();
            return;
        }

        var dipOrigin = WpfDpiHelper.PixelsToDips(this, clientBounds.Left + _region.X, clientBounds.Top + _region.Y);
        var dipSize = WpfDpiHelper.PixelsToDipsSize(this, _region.Width, _region.Height);

        Left = dipOrigin.X;
        Top = dipOrigin.Y;
        Width = dipSize.Width;
        Height = dipSize.Height;

        if (!IsVisible)
        {
            Show();
        }
    }

    public void HideOverlay()
    {
        Hide();
    }

    private void CaptureOverlayWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var styles = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        styles |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(styles));
    }
}