using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace AIEverything.App;

public partial class App : Application
{
    private const string WindowTitle = "AIEverything";
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\AIEverything.Desktop",
            createdNew: out var createdNew);
        _ownsInstanceMutex = createdNew;
        if (!createdNew)
        {
            ActivateExistingWindow();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            if (_ownsInstanceMutex)
            {
                _instanceMutex.ReleaseMutex();
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"AIEverything 遇到错误：{e.Exception.Message}",
            "AIEverything",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void ActivateExistingWindow()
    {
        var window = FindWindow(null, WindowTitle);
        if (window == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(window, 9);
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
