using System.Runtime.InteropServices;
using ExplorerPlus.Indexing;
using ExplorerPlus.Interop;

namespace ExplorerPlus.IndexService;

/// <summary>The Win32 service plumbing (StartServiceCtrlDispatcher, ServiceMain, the control handler), without NuGet.</summary>
internal static class ServiceRunner
{
    private static readonly ManualResetEventSlim Stop = new();
    private static readonly ServiceNative.ServiceMain MainDelegate = ServiceMain;
    private static readonly ServiceNative.HandlerEx HandlerDelegate = Handler;
    private static IntPtr _statusHandle;
    private static SERVICE_STATUS _status = new() { ServiceType = ServiceNative.SERVICE_WIN32_OWN_PROCESS };

    public static int Run()
    {
        SERVICE_TABLE_ENTRY[] table =
        [
            new() { ServiceName = IndexClient.ServiceName, ServiceProc = Marshal.GetFunctionPointerForDelegate(MainDelegate) },
            new() { ServiceName = null, ServiceProc = IntPtr.Zero },
        ];
        // Blocks until the service has stopped.
        return ServiceNative.StartServiceCtrlDispatcherW(table) ? 0 : Marshal.GetLastWin32Error();
    }

    private static void ServiceMain(uint argc, IntPtr argv)
    {
        _statusHandle = ServiceNative.RegisterServiceCtrlHandlerExW(IndexClient.ServiceName, HandlerDelegate, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;
        SetState(ServiceNative.SERVICE_START_PENDING, 0);
        Worker? worker = null;
        try
        {
            worker = Worker.Start();
            SetState(ServiceNative.SERVICE_RUNNING, ServiceNative.SERVICE_ACCEPT_STOP | ServiceNative.SERVICE_ACCEPT_SHUTDOWN);
            Stop.Wait();
            SetState(ServiceNative.SERVICE_STOP_PENDING, 0);
        }
        catch (Exception ex)
        {
            Log.Write($"Service failed: {ex}");
            _status.Win32ExitCode = 1064; // ERROR_EXCEPTION_IN_SERVICE
        }
        finally
        {
            worker?.Dispose();
            SetState(ServiceNative.SERVICE_STOPPED, 0);
        }
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ServiceNative.SERVICE_CONTROL_STOP:
            case ServiceNative.SERVICE_CONTROL_SHUTDOWN:
                Stop.Set();
                return 0;
            case ServiceNative.SERVICE_CONTROL_INTERROGATE:
                return 0;
            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    private static void SetState(uint state, uint accepted)
    {
        _status.CurrentState = state;
        _status.ControlsAccepted = accepted;
        _status.WaitHint = state is ServiceNative.SERVICE_START_PENDING or ServiceNative.SERVICE_STOP_PENDING ? 10_000u : 0;
        _status.CheckPoint = _status.WaitHint == 0 ? 0 : _status.CheckPoint + 1;
        ServiceNative.SetServiceStatus(_statusHandle, ref _status);
    }
}
