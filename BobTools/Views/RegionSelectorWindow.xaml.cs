using BobTools.Models;
using BobTools.Utils;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BobTools.Views;

public partial class RegionSelectorWindow : Window
{
    private readonly string _captureProfile;
    private readonly CaptureRegion? _initialRegion;
    private readonly (int Width, int Height)? _fixedRegionSize;
    private System.Windows.Point? _startPoint;
    private System.Windows.Point? _dragOffset;
    private bool _isDraggingFixedRegion;
    public CaptureRegion? SelectedRegion { get; private set; }

    public RegionSelectorWindow(string captureProfile, CaptureRegion? initialRegion = null)
    {
        _captureProfile = captureProfile;
        _initialRegion = initialRegion;
        _fixedRegionSize = CaptureProfileCatalog.GetFixedRegionSize(captureProfile);
        InitializeComponent();
        Loaded += RegionSelectorWindow_Loaded;
        KeyDown += RegionSelectorWindow_KeyDown;
    }

    private void RegionSelectorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SelectedRegion = null;
        _startPoint = null;
        _dragOffset = null;
        _isDraggingFixedRegion = false;
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
        ConfigureInstructions();
        InitializeSelectionVisual();
        Activate();
        Focus();
    }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_fixedRegionSize is not null)
        {
            var current = e.GetPosition(this);
            if (SelectionRectangle.Visibility == Visibility.Visible
                && current.X >= Canvas.GetLeft(SelectionRectangle)
                && current.X <= Canvas.GetLeft(SelectionRectangle) + SelectionRectangle.Width
                && current.Y >= Canvas.GetTop(SelectionRectangle)
                && current.Y <= Canvas.GetTop(SelectionRectangle) + SelectionRectangle.Height)
            {
                _dragOffset = new System.Windows.Point(current.X - Canvas.GetLeft(SelectionRectangle), current.Y - Canvas.GetTop(SelectionRectangle));
            }
            else
            {
                _dragOffset = new System.Windows.Point(SelectionRectangle.Width / 2d, SelectionRectangle.Height / 2d);
                UpdateFixedSelectionVisual(current);
            }

            _isDraggingFixedRegion = true;
            RootCanvas.CaptureMouse();
            return;
        }

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
        if (_fixedRegionSize is not null)
        {
            if (!_isDraggingFixedRegion || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            UpdateFixedSelectionVisual(e.GetPosition(this));
            return;
        }

        if (_startPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        UpdateSelectionVisual(_startPoint.Value, current);
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_fixedRegionSize is not null)
        {
            if (!_isDraggingFixedRegion)
            {
                return;
            }

            RootCanvas.ReleaseMouseCapture();
            _isDraggingFixedRegion = false;
            SaveFixedSelection();
            return;
        }

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

    private void ConfigureInstructions()
    {
        if (_fixedRegionSize is null)
        {
            InstructionTitleTextBlock.Text = "Drag inside the Star Citizen window";
            InstructionBodyTextBlock.Text = "Release mouse to save a region relative to Star Citizen, or press Esc to cancel.";
            return;
        }

        InstructionTitleTextBlock.Text = $"Place the fixed {CaptureProfileCatalog.SignatureFixedWidth} x {CaptureProfileCatalog.SignatureFixedHeight} scan box";
        InstructionBodyTextBlock.Text = "Move the fixed box so the signature icon and value are centered inside it, then release mouse to save. Press Esc to cancel.";
    }

    private void InitializeSelectionVisual()
    {
        if (_fixedRegionSize is null)
        {
            return;
        }

        var size = WpfDpiHelper.PixelsToDipsSize(this, _fixedRegionSize.Value.Width, _fixedRegionSize.Value.Height);
        SelectionRectangle.Width = size.Width;
        SelectionRectangle.Height = size.Height;

        double left;
        double top;

        if (_initialRegion is { IsValid: true })
        {
            var origin = WpfDpiHelper.PixelsToDips(this, _initialRegion.X, _initialRegion.Y);
            left = origin.X;
            top = origin.Y;
        }
        else
        {
            left = (ActualWidth - size.Width) / 2d;
            top = (ActualHeight - size.Height) / 2d;
        }

        SetFixedSelectionPosition(left, top);
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void UpdateFixedSelectionVisual(System.Windows.Point current)
    {
        var offset = _dragOffset ?? new System.Windows.Point(SelectionRectangle.Width / 2d, SelectionRectangle.Height / 2d);
        var left = current.X - offset.X;
        var top = current.Y - offset.Y;
        SetFixedSelectionPosition(left, top);
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void SetFixedSelectionPosition(double left, double top)
    {
        var maxLeft = Math.Max(0d, ActualWidth - SelectionRectangle.Width);
        var maxTop = Math.Max(0d, ActualHeight - SelectionRectangle.Height);
        Canvas.SetLeft(SelectionRectangle, Math.Max(0d, Math.Min(maxLeft, left)));
        Canvas.SetTop(SelectionRectangle, Math.Max(0d, Math.Min(maxTop, top)));
    }

    private void SaveFixedSelection()
    {
        var topLeft = new System.Windows.Point(Canvas.GetLeft(SelectionRectangle), Canvas.GetTop(SelectionRectangle));
        var pixelTopLeft = WpfDpiHelper.DipsToPixels(this, topLeft);
        SelectedRegion = new CaptureRegion
        {
            X = pixelTopLeft.X,
            Y = pixelTopLeft.Y,
            Width = _fixedRegionSize!.Value.Width,
            Height = _fixedRegionSize.Value.Height
        };

        DialogResult = true;
        Close();
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