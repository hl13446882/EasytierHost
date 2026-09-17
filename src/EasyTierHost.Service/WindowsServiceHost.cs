using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EasyTierHost.Service;

/// <summary>
/// Minimal SCM host used to keep EasyTierHost self-contained. The service owns one worker and
/// translates STOP/SHUTDOWN/PRESHUTDOWN into cancellation so network journals can roll back.
/// </summary>
internal static class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceAcceptPreShutdown = 0x00000100;
    private const uint ControlStop = 0x00000001;
    private const uint ControlInterrogate = 0x00000004;
    private const uint ControlShutdown = 0x00000005;
    private const uint ControlPreShutdown = 0x0000000F;
    private const uint ErrorServiceSpecificError = 1066;

    private static string _serviceName = "EasyTierHost";
    private static Func<CancellationToken, Task<int>>? _worker;
    private static CancellationTokenSource? _stop;
    private static ServiceMainDelegate? _serviceMain;
    private static HandlerExDelegate? _handler;
    private static nint _statusHandle;
    private static int _exitCode;
    private static uint _currentState = ServiceStopped;
    private static uint _checkpoint;

    public static int Run(string serviceName, Func<CancellationToken, Task<int>> worker)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows SCM service mode is Windows-only");
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(worker);

        _serviceName = serviceName;
        _worker = worker;
        _exitCode = 1;
        _checkpoint = 0;
        _serviceMain = ServiceMain;

        var table = new[]
        {
            new ServiceTableEntry { ServiceName = serviceName, ServiceMain = _serviceMain },
            new ServiceTableEntry { ServiceName = null, ServiceMain = null }
        };

        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return _exitCode;
    }

    private static void ServiceMain(int argc, nint argv)
    {
        _handler = ServiceControlHandler;
        _stop = new CancellationTokenSource();
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, _handler, nint.Zero);
        if (_statusHandle == nint.Zero)
        {
            _exitCode = 1;
            return;
        }

        Report(ServiceStartPending, 20_000);
        Report(ServiceRunning, 0);
        try
        {
            _exitCode = _worker!(_stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _exitCode = 0;
        }
        catch
        {
            // The worker writes sanitized runtime state; do not copy exception text into SCM/logs.
            _exitCode = 1;
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
            Report(ServiceStopped, 0, _exitCode);
        }
    }

    private static uint ServiceControlHandler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
            case ControlPreShutdown:
                if (_currentState is ServiceRunning or ServiceStartPending)
                {
                    Report(ServiceStopPending, 30_000);
                    _stop?.Cancel();
                }
                return 0;
            case ControlInterrogate:
                Report(_currentState, _currentState == ServiceStopPending ? 30_000u : 0u);
                return 0;
            default:
                return 0;
        }
    }

    private static void Report(uint state, uint waitHint, int serviceExitCode = 0)
    {
        if (_statusHandle == nint.Zero) return;
        _currentState = state;
        var pending = state is ServiceStartPending or ServiceStopPending;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == ServiceRunning ? ServiceAcceptStop | ServiceAcceptShutdown | ServiceAcceptPreShutdown : 0,
            Win32ExitCode = serviceExitCode == 0 ? 0u : ErrorServiceSpecificError,
            ServiceSpecificExitCode = serviceExitCode <= 0 ? 0u : (uint)serviceExitCode,
            CheckPoint = pending ? ++_checkpoint : 0,
            WaitHint = waitHint
        };
        if (!SetServiceStatus(_statusHandle, ref status) && state != ServiceStopped)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServiceName;
        public ServiceMainDelegate? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argc, nint argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerExDelegate(uint control, uint eventType, nint eventData, nint context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerEx(string serviceName, HandlerExDelegate handlerProc, nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint serviceStatusHandle, ref ServiceStatus serviceStatus);
}
