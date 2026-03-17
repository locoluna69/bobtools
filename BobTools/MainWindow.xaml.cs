using BobTools.Services;
using BobTools.ViewModels;
using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Interop;

namespace BobTools;

public partial class MainWindow : Window
{
    private readonly IHotkeyService _hotkeyService;
    private readonly MainViewModel _viewModel;
    private readonly Models.AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Path.Combine(localAppData, "BobTools");
        var settingsPath = Path.Combine(appData, "settings.json");
        var tessDataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tessdata");

        MigrateLegacySettings(localAppData, settingsPath);

        var settingsService = new SettingsService(settingsPath);

        _hotkeyService = new HotkeyService();
        _settings = settingsService.Load();
        _viewModel = new MainViewModel(
            settingsService,
            new ScreenCaptureService(),
            new OcrService(tessDataPath));

        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private static void MigrateLegacySettings(string localAppData, string newSettingsPath)
    {
        if (File.Exists(newSettingsPath))
        {
            return;
        }

        var legacySettingsPath = Path.Combine(localAppData, "BobTools", "settings.json");
        if (!File.Exists(legacySettingsPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newSettingsPath)!);
        File.Copy(legacySettingsPath, newSettingsPath, overwrite: false);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionOverlayAtTop();

        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);

        _hotkeyService.Register(helper.Handle, _settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;
    }

    private void PositionOverlayAtTop()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(MinWidth, workArea.Width - 32), 1600);
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + 8;
    }

    private async void HotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        await _viewModel.CaptureAndReviewAsync();
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OfflineDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files is { Length: > 0 } && files.All(IsSupportedOfflineImage))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }

        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OfflineDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        var files = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
        if (files is not { Length: > 0 })
        {
            return;
        }

        if (files.Any(filePath => !IsSupportedOfflineImage(filePath)))
        {
            _viewModel.Status = "Unsupported image type. Use PNG, JPG, JPEG, or BMP.";
            return;
        }

        _viewModel.LoadOfflineTestImages(files);
    }

    private static bool IsSupportedOfflineImage(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        handled = _hotkeyService.ProcessWindowMessage(msg, wParam, lParam);
        return IntPtr.Zero;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
        _hotkeyService.Dispose();
    }
}
