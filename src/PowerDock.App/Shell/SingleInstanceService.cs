using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using PowerDock.App.Interop;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Shell
{
    public sealed class SingleInstanceService : IDisposable
    {
        private readonly string _mutexName;
        private readonly string _pipeName;

        private Mutex _mutex;
        private bool _ownsMutex;
        private bool _isFirstInstance;
        private bool _disposed;

        private Action _onActivateReceived;
        private Thread _listenerThread;
        private ManualResetEvent _stopEvent;
        private NamedPipeServerStream _currentServerStream;

        public bool IsFirstInstance
        {
            get { return _isFirstInstance; }
            private set { _isFirstInstance = value; }
        }

        public string MutexName
        {
            get { return _mutexName; }
        }

        public string PipeName
        {
            get { return _pipeName; }
        }

        public SingleInstanceService()
            : this(GetDefaultMutexName(), GetDefaultPipeName())
        {
        }

        public SingleInstanceService(string mutexName, string pipeName)
        {
            _mutexName = mutexName;
            _pipeName = pipeName;
            _stopEvent = new ManualResetEvent(false);
        }

        public static string GetDefaultMutexName()
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            return "Local\\PowerDock_Mutex_" + sessionId;
        }

        public static string GetDefaultPipeName()
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            return "PowerDock_Pipe_" + sessionId;
        }

        public bool TryStartAsFirstInstance(Action onActivateReceived)
        {
            bool createdNew = false;
            try
            {
                _mutex = new Mutex(true, _mutexName, out createdNew);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Mutex creation error: " + ex.Message);
                createdNew = false;
            }

            if (createdNew)
            {
                _ownsMutex = true;
                _isFirstInstance = true;
                _onActivateReceived = onActivateReceived;
                StartPipeServerListener();
                AppLogger.Info("Acquired single-instance mutex as primary instance: " + _mutexName);
                return true;
            }
            else
            {
                _isFirstInstance = false;
                if (_mutex != null)
                {
                    try { _mutex.Close(); } catch { }
                    _mutex = null;
                }

                AppLogger.Info("Mutex is already held by primary instance. Notifying primary instance via pipe: " + _pipeName);
                SendActivateToPrimary();
                return false;
            }
        }

        public bool SendActivateToPrimary()
        {
            return SendActivateToPrimary(1500);
        }

        public bool SendActivateToPrimary(int timeoutMs)
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                {
                    int startTime = Environment.TickCount;
                    bool connected = false;

                    while (!connected && (Environment.TickCount - startTime < timeoutMs))
                    {
                        try
                        {
                            int remaining = timeoutMs - (Environment.TickCount - startTime);
                            if (remaining <= 0) break;
                            client.Connect(Math.Min(remaining, 500));
                            connected = true;
                        }
                        catch (TimeoutException)
                        {
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(50);
                        }
                    }

                    if (!connected)
                    {
                        AppLogger.Warn("Failed to connect to primary instance named pipe within timeout: " + timeoutMs + "ms");
                        return false;
                    }

                    try
                    {
                        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("AllowSetForegroundWindow warning: " + ex.Message);
                    }

                    StreamWriter writer = new StreamWriter(client, Encoding.UTF8);
                    StreamReader reader = new StreamReader(client, Encoding.UTF8);

                    int currentPid = Process.GetCurrentProcess().Id;
                    writer.WriteLine("PID:" + currentPid);
                    writer.WriteLine("activate");
                    writer.Flush();

                    string response = reader.ReadLine();
                    AppLogger.Info("Primary instance response: " + (response ?? "null"));
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exception in SendActivateToPrimary", ex);
                return false;
            }
        }

        public void ActivateWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            try
            {
                if (NativeMethods.IsIconic(hWnd))
                {
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
                }
                else
                {
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
                }

                bool broughtToForeground = NativeMethods.SetForegroundWindow(hWnd);
                if (!broughtToForeground)
                {
                    AppLogger.Warn("SetForegroundWindow denied by system; flashing window taskbar button.");
                    NativeMethods.FLASHWINFO fwi = new NativeMethods.FLASHWINFO();
                    fwi.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.FLASHWINFO));
                    fwi.hwnd = hWnd;
                    fwi.dwFlags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG;
                    fwi.uCount = 3;
                    fwi.dwTimeout = 0;
                    NativeMethods.FlashWindowEx(ref fwi);
                }
                else
                {
                    AppLogger.Info("Window brought to foreground successfully.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error activating window: " + hWnd, ex);
            }
        }

        private void StartPipeServerListener()
        {
            _listenerThread = new Thread(ListenLoop);
            _listenerThread.IsBackground = true;
            _listenerThread.Name = "PowerDock_PipeServer";
            _listenerThread.Start();
        }

        private void ListenLoop()
        {
            while (!_disposed)
            {
                NamedPipeServerStream server = null;
                try
                {
                    PipeSecurity ps = CreatePipeSecurity();
                    if (ps != null)
                    {
                        // .NET Core+ 起 NamedPipeServerStream 不再提供带 PipeSecurity 的构造函数，
                        // 改为通过 NamedPipeServerStreamAcl.Create 传入同一套 ACL。
                        server = NamedPipeServerStreamAcl.Create(
                            _pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous,
                            4096,
                            4096,
                            ps);
                    }
                    else
                    {
                        server = new NamedPipeServerStream(
                            _pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);
                    }

                    _currentServerStream = server;

                    IAsyncResult ar = server.BeginWaitForConnection(null, null);
                    int waitIndex = WaitHandle.WaitAny(new WaitHandle[] { ar.AsyncWaitHandle, _stopEvent });

                    if (waitIndex == 1 || _disposed)
                    {
                        try { server.Close(); } catch { }
                        break;
                    }

                    server.EndWaitForConnection(ar);
                    HandleClientConnection(server);
                }
                catch (Exception ex)
                {
                    if (_disposed) break;
                    AppLogger.Warn("Pipe server loop exception: " + ex.Message);
                    Thread.Sleep(100);
                }
                finally
                {
                    if (server != null)
                    {
                        try { server.Close(); } catch { }
                    }
                    _currentServerStream = null;
                }
            }
        }

        private void HandleClientConnection(NamedPipeServerStream server)
        {
            try
            {
                try
                {
                    string clientUser = server.GetImpersonationUserName();
                    string currentUser = Environment.UserName;
                    if (!string.IsNullOrEmpty(clientUser) && !string.IsNullOrEmpty(currentUser))
                    {
                        if (clientUser.IndexOf(currentUser, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            AppLogger.Warn("Rejected connection from non-current user: " + clientUser);
                            return;
                        }
                    }
                }
                catch
                {
                }

                StreamReader reader = new StreamReader(server, Encoding.UTF8);
                StreamWriter writer = new StreamWriter(server, Encoding.UTF8);

                string clientPid = null;
                bool activateRequested = false;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("PID:", StringComparison.OrdinalIgnoreCase))
                    {
                        clientPid = line.Substring(4).Trim();
                    }
                    else if (string.Compare(line, "activate", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        activateRequested = true;
                        break;
                    }
                    else
                    {
                        int parsed;
                        if (int.TryParse(line, out parsed))
                        {
                            clientPid = line;
                        }
                    }
                }

                if (activateRequested)
                {
                    AppLogger.Info(string.Format("Received activate request from client PID: {0}", clientPid ?? "unknown"));
                    writer.WriteLine("OK");
                    writer.Flush();

                    if (_onActivateReceived != null)
                    {
                        ExecuteOnUIThread(_onActivateReceived);
                    }
                }
                else
                {
                    writer.WriteLine("UNKNOWN_COMMAND");
                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Error handling pipe client connection: " + ex.Message);
            }
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            try
            {
                PipeSecurity ps = new PipeSecurity();
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                if (id != null && id.User != null)
                {
                    ps.AddAccessRule(new PipeAccessRule(id.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    return ps;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Failed to initialize PipeSecurity: " + ex.Message);
            }
            return null;
        }

        private static void ExecuteOnUIThread(Action action)
        {
            if (action == null) return;

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    app.Dispatcher.BeginInvoke(action);
                }
            }
            else
            {
                action();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                if (_stopEvent != null)
                {
                    try { _stopEvent.Set(); } catch { }
                }

                if (_currentServerStream != null)
                {
                    try { _currentServerStream.Close(); } catch { }
                    _currentServerStream = null;
                }

                if (_listenerThread != null)
                {
                    try
                    {
                        if (_listenerThread.IsAlive)
                        {
                            _listenerThread.Join(500);
                        }
                    }
                    catch { }
                    _listenerThread = null;
                }

                if (_stopEvent != null)
                {
                    try { _stopEvent.Close(); } catch { }
                    _stopEvent = null;
                }

                if (_mutex != null)
                {
                    if (_ownsMutex)
                    {
                        try
                        {
                            _mutex.ReleaseMutex();
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("Exception releasing mutex: " + ex.Message);
                        }
                        _ownsMutex = false;
                    }

                    try { _mutex.Close(); } catch { }
                    _mutex = null;
                }
            }
        }
    }
}
