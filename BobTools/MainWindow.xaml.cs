using BobTools.Interop;
using BobTools.Services;
using BobTools.Utils;
using BobTools.ViewModels;
using BobTools.Views;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows;
using System.Windows.Interop;

namespace BobTools;

public partial class MainWindow : Window
{
    private const double FullModeDefaultWidth = 1460;
    private const double FullModeDefaultHeight = 620;
    private const double FullModeMinWidth = 980;
    private const double FullModeMinHeight = 420;
    private const double CompactModeBaseDefaultWidth = 560;
    private const double CompactModeBaseDefaultHeight = 248;
    private const double CompactModeBaseMinWidth = 420;
    private const double CompactModeBaseMinHeight = 220;
    private const double CompactModeMinimumWidthFloor = 280;
    private const double CompactModeMinimumHeightFloor = 140;
    private const double CompactModeChromeWidth = 40;
    private const double CompactModeChromeHeight = 44;

    private readonly IHotkeyService _hotkeyService;
    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly Models.AppSettings _settings;
    private bool _isApplyingWindowModeLayout;

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
        _settingsService = settingsService;
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
        SizeChanged += MainWindow_SizeChanged;
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
        ApplyWindowModeLayout(isInitialLayout: true);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);

        TryRegisterHotkey();
    }

    private void ApplyWindowModeLayout(bool isInitialLayout, bool preferPersistedSize = true)
    {
        var workArea = SystemParameters.WorkArea;
        _isApplyingWindowModeLayout = true;

        try
        {
            if (_viewModel.IsCompactMode)
            {
                var compactMinWidth = GetCompactMinWidth();
                var compactMinHeight = GetCompactMinHeight();
                var compactDefaultWidth = GetCompactDefaultWidth();
                var compactDefaultHeight = GetCompactDefaultHeight();
                var targetWidth = preferPersistedSize && _settings.CompactModeWindowWidth > 0
                    ? _settings.CompactModeWindowWidth
                    : isInitialLayout || Width > 760
                        ? compactDefaultWidth
                        : Width;
                var targetHeight = preferPersistedSize && _settings.CompactModeWindowHeight > 0
                    ? _settings.CompactModeWindowHeight
                    : isInitialLayout || Height > 420
                        ? compactDefaultHeight
                        : Height;

                MinWidth = compactMinWidth;
                MinHeight = compactMinHeight;
                Width = Math.Max(compactMinWidth, targetWidth);
                Height = Math.Max(compactMinHeight, targetHeight);
            }
            else
            {
                var fullDefaultWidth = Math.Min(Math.Max(FullModeMinWidth, workArea.Width - 32), 1600);
                var targetWidth = preferPersistedSize && _settings.FullModeWindowWidth > 0
                    ? _settings.FullModeWindowWidth
                    : isInitialLayout || Width < FullModeMinWidth
                        ? fullDefaultWidth
                        : Width;
                var targetHeight = preferPersistedSize && _settings.FullModeWindowHeight > 0
                    ? _settings.FullModeWindowHeight
                    : isInitialLayout || Height < FullModeMinHeight
                        ? FullModeDefaultHeight
                        : Height;

                MinWidth = FullModeMinWidth;
                MinHeight = FullModeMinHeight;
                Width = Math.Max(FullModeMinWidth, targetWidth);
                Height = Math.Max(FullModeMinHeight, targetHeight);
            }

            Width = Math.Min(Width, workArea.Width - 16);
            Height = Math.Min(Height, workArea.Height - 16);

            if (isInitialLayout)
            {
                Left = workArea.Left + ((workArea.Width - Width) / 2);
                Top = workArea.Top + 8;
                return;
            }

            Left = Math.Max(workArea.Left + 8, Math.Min(Left, workArea.Right - Width - 8));
            Top = Math.Max(workArea.Top + 8, Math.Min(Top, workArea.Bottom - Height - 8));
        }
        finally
        {
            _isApplyingWindowModeLayout = false;
        }
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

        if (e.PropertyName == nameof(MainViewModel.IsCompactMode))
        {
            ApplyWindowModeLayout(isInitialLayout: false, preferPersistedSize: true);
        }

        if (e.PropertyName == nameof(MainViewModel.CompactModeScale) && _viewModel.IsCompactMode)
        {
            ApplyWindowModeLayout(isInitialLayout: false, preferPersistedSize: false);
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isApplyingWindowModeLayout || WindowState != WindowState.Normal)
        {
            return;
        }

        var widthChanged = false;
        var heightChanged = false;

        if (_viewModel.IsCompactMode)
        {
            var compactMinWidth = GetCompactMinWidth();
            var compactMinHeight = GetCompactMinHeight();
            var nextWidth = Math.Max(compactMinWidth, Width);
            var nextHeight = Math.Max(compactMinHeight, Height);
            widthChanged = !AreClose(_settings.CompactModeWindowWidth, nextWidth);
            heightChanged = !AreClose(_settings.CompactModeWindowHeight, nextHeight);
            _settings.CompactModeWindowWidth = nextWidth;
            _settings.CompactModeWindowHeight = nextHeight;
        }
        else
        {
            var nextWidth = Math.Max(FullModeMinWidth, Width);
            var nextHeight = Math.Max(FullModeMinHeight, Height);
            widthChanged = !AreClose(_settings.FullModeWindowWidth, nextWidth);
            heightChanged = !AreClose(_settings.FullModeWindowHeight, nextHeight);
            _settings.FullModeWindowWidth = nextWidth;
            _settings.FullModeWindowHeight = nextHeight;
        }

        if (widthChanged || heightChanged)
        {
            _settingsService.Save(_settings);
        }
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.5;
    }

    private double GetCompactDefaultWidth()
    {
        return Math.Max(CompactModeMinimumWidthFloor, Math.Ceiling((CompactModeBaseDefaultWidth * _viewModel.CompactModeScale) + CompactModeChromeWidth));
    }

    private double GetCompactDefaultHeight()
    {
        return Math.Max(CompactModeMinimumHeightFloor, Math.Ceiling((CompactModeBaseDefaultHeight * _viewModel.CompactModeScale) + CompactModeChromeHeight));
    }

    private double GetCompactMinWidth()
    {
        return Math.Max(CompactModeMinimumWidthFloor, Math.Ceiling((CompactModeBaseMinWidth * _viewModel.CompactModeScale) + CompactModeChromeWidth));
    }

    private double GetCompactMinHeight()
    {
        return Math.Max(CompactModeMinimumHeightFloor, Math.Ceiling((CompactModeBaseMinHeight * _viewModel.CompactModeScale) + CompactModeChromeHeight));
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

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = this,
            DataContext = _viewModel
        };

        settingsWindow.ShowDialog();
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
