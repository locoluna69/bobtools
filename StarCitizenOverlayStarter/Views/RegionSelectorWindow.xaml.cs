using StarCitizenOverlayStarter.Models;
using StarCitizenOverlayStarter.Utils;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StarCitizenOverlayStarter.Views;

public partial class RegionSelectorWindow : Window
{
    private System.Windows.Point? _startPoint;
    public CaptureRegion? SelectedRegion { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        Loaded += RegionSelectorWindow_Loaded;
        KeyDown += RegionSelectorWindow_KeyDown;
    }

    private void RegionSelectorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SelectedRegion = null;
        _startPoint = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;

        if (!StarCitizenWindowLocator.TryGetClientBounds(out var clientBounds, out var errorMessage))
        {
            System.Windows.MessageBox.Show(errorMessage, "Star Citizen Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        var dipBounds = WpfDpiHelper.PixelsToDips(this, clientBounds);

        Left = dipBounds.Left;
        Top = dipBounds.Top;
        Width = dipBounds.Width;
        Height = dipBounds.Height;
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        Canvas.SetLeft(SelectionRectangle, _startPoint.Value.X);
        Canvas.SetTop(SelectionRectangle, _startPoint.Value.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        SelectionRectangle.Visibility = Visibility.Visible;
        RootCanvas.CaptureMouse();
    }

    private void RootCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_startPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        UpdateSelectionVisual(_startPoint.Value, current);
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint is null)
        {
            return;
        }

        var end = e.GetPosition(this);
        RootCanvas.ReleaseMouseCapture();

        var left = Math.Min(_startPoint.Value.X, end.X);
        var top = Math.Min(_startPoint.Value.Y, end.Y);
        var width = Math.Abs(end.X - _startPoint.Value.X);
        var height = Math.Abs(end.Y - _startPoint.Value.Y);

        var pixelTopLeft = WpfDpiHelper.DipsToPixels(this, new System.Windows.Point(left, top));
        var pixelBottomRight = WpfDpiHelper.DipsToPixels(this, new System.Windows.Point(left + width, top + height));
        var pixelWidth = Math.Abs(pixelBottomRight.X - pixelTopLeft.X);
        var pixelHeight = Math.Abs(pixelBottomRight.Y - pixelTopLeft.Y);

        if (pixelWidth >= 5 && pixelHeight >= 5)
        {
            SelectedRegion = new CaptureRegion
            {
                X = pixelTopLeft.X,
                Y = pixelTopLeft.Y,
                Width = pixelWidth,
                Height = pixelHeight
            };

            DialogResult = true;
            Close();
            return;
        }

        SelectionRectangle.Visibility = Visibility.Collapsed;
        _startPoint = null;
    }

    private void UpdateSelectionVisual(System.Windows.Point start, System.Windows.Point current)
    {
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
    }

    private void RegionSelectorWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}