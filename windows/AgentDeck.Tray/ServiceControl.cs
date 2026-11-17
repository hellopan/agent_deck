using System.Runtime.InteropServices;

namespace AgentDeck.Tray;

// The settings card reports the service by asking the service manager, which is
// the only source that knows whether it is actually running. The registry key
// only says it is registered, and a registered service that failed to start
// looks identical there.
internal static class ServiceControl
{
    internal const string Name = "AgentDeckLab";

    private const uint ManagerConnect = 0x0001;
    private const uint QueryStatus = 0x0004;
    private const uint Running = 4;

    internal readonly record struct State(bool Installed, bool Running);

    internal static State Query(string name = Name)
    {
        var manager = OpenSCManager(null, null, ManagerConnect);
        if (manager == IntPtr.Zero) return default;
        try
        {
            var service = OpenService(manager, name, QueryStatus);
            if (service == IntPtr.Zero) return default;
            try
            {
                if (!QueryServiceStatus(service, out var status)) return new State(true, false);
                return new State(true, status.CurrentState == Running);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
