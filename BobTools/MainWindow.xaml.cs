using BobTools.Interop;
using BobTools.Services;
using BobTools.Utils;
using BobTools.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Text;
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
        var sessionHistoryService = new SessionHistoryService(Path.Combine(appData, "Sessions"), App.SessionId);
        sessionHistoryService.Initialize();

        _hotkeyService = new HotkeyService();
        _settings = settingsService.Load();
        _viewModel = new MainViewModel(
            settingsService,
            sessionHistoryService,
            new ScreenCaptureService(),
            new OcrService(tessDataPath),
            new RsiProfileImageService(Path.Combine(appData, "ProfileImages")));

        DataContext = _viewModel;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
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
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);

        TryRegisterHotkey();
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

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedHotkeyModifiers)
            || e.PropertyName == nameof(MainViewModel.SelectedHotkeyVirtualKey))
        {
            if (!_viewModel.IsRecordingHotkey)
            {
                TryRegisterHotkey();
            }
        }

        if (e.PropertyName == nameof(MainViewModel.IsRecordingHotkey))
        {
            if (_viewModel.IsRecordingHotkey)
            {
                _hotkeyService.Unregister();
                _viewModel.SetHotkeyRegistrationStatus("Hotkey recorder is armed. Press the new shortcut inside BobTools, or press Escape to cancel.");
                WriteHotkeyDiagnostic("RecorderArmed", "The hotkey recorder is waiting for the next shortcut input.", null);
                Activate();
                Focus();
            }
            else
            {
                TryRegisterHotkey();
            }
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingHotkey)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            _viewModel.CancelHotkeyRecording("Hotkey recording canceled.");
            e.Handled = true;
            return;
        }

        if (IsModifierOnlyKey(key))
        {
            e.Handled = true;
            return;
        }

        if (!TryGetNativeModifiers(Keyboard.Modifiers, out var modifiers))
        {
            _viewModel.SetHotkeyRecorderSummary("Use Alt, Ctrl+Shift, Ctrl+Alt, Alt+Shift, or Ctrl+Shift+Alt with a letter or function key.");
            e.Handled = true;
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0 || !HotkeyCatalog.IsSupported(modifiers, (uint)virtualKey))
        {
            _viewModel.SetHotkeyRecorderSummary("Unsupported shortcut. Try a letter or function key with one of the supported modifier combinations.");
            e.Handled = true;
            return;
        }

        var formattedHotkey = HotkeyCatalog.Format(modifiers, (uint)virtualKey);
        WriteHotkeyDiagnostic("RecorderCaptured", $"Captured shortcut candidate {formattedHotkey}.", null);
        _viewModel.CompleteHotkeyRecording(modifiers, (uint)virtualKey);
        e.Handled = true;
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
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _hotkeyService.HotkeyPressed -= HotkeyService_HotkeyPressed;
        _viewModel.Dispose();
        _hotkeyService.Dispose();
    }

    private void TryRegisterHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var formattedHotkey = HotkeyCatalog.Format(_viewModel.SelectedHotkeyModifiers, _viewModel.SelectedHotkeyVirtualKey);

        if (handle == IntPtr.Zero)
        {
            const string message = "Hotkey registration is waiting for the overlay window to finish initializing.";
            _viewModel.SetHotkeyRegistrationStatus(message);
            WriteHotkeyDiagnostic("WaitingForHandle", message, null);
            return;
        }

        try
        {
            _hotkeyService.Register(handle, _viewModel.SelectedHotkeyModifiers, _viewModel.SelectedHotkeyVirtualKey);
            var message = $"Global hotkey active: {formattedHotkey}. If Star Citizen still blocks it, use borderless windowed mode or switch to {HotkeyCatalog.RecommendedDisplay}.";
            _viewModel.SetHotkeyRegistrationStatus(message);
            WriteHotkeyDiagnostic("Registered", message, null);
        }
        catch (Win32Exception ex)
        {
            var message = $"Could not register {formattedHotkey} (Win32 {ex.NativeErrorCode}). Try {HotkeyCatalog.RecommendedDisplay} or another function-key chord.";
            _viewModel.SetHotkeyRegistrationStatus(message);
            WriteHotkeyDiagnostic("RegistrationFailed", message, ex);
        }
    }

    private static void WriteHotkeyDiagnostic(string eventName, string message, Exception? exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BobTools",
                "logs");

            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "hotkey.log");
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTime.Now.ToString("O")).Append("] ");
            builder.Append(eventName).Append(" | Session ").Append(App.SessionId).Append(" | ");
            builder.AppendLine(message);

            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            File.AppendAllText(logPath, builder.ToString());
        }
        catch
        {
        }
    }

    private static bool TryGetNativeModifiers(ModifierKeys modifiers, out uint nativeModifiers)
    {
        nativeModifiers = 0;

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            return false;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            nativeModifiers |= NativeMethods.MOD_ALT;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            nativeModifiers |= NativeMethods.MOD_CONTROL;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            nativeModifiers |= NativeMethods.MOD_SHIFT;
        }

        var capturedModifiers = nativeModifiers;
        return capturedModifiers != 0 && HotkeyCatalog.ModifierOptions.Any(option => option.Value == capturedModifiers);
    }

    private static bool IsModifierOnlyKey(Key key)
    {
        return key is Key.LeftAlt
            or Key.RightAlt
            or Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftShift
            or Key.RightShift
            or Key.LWin
            or Key.RWin;
    }
}
