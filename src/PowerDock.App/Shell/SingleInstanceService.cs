using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PowerDock.App.Interop;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Shell
{
    /// <summary>Result of one activation request; the primary returns it to the secondary over the pipe.</summary>
    public enum ActivationResult
    {
        Activated = 0,
        Denied = 1,
        NoWindow = 2,
        IdentityRejected = 3,
        ProtocolTimeout = 4
    }

    /// <summary>
    /// Exit-code contract shared by Program / --help / tools/verify-single-instance.ps1.
    /// 0 = success (first instance running, or a secondary activated it), 20 = no primary instance,
    /// 21 = primary not ready yet, 22 = foreground activation denied, 23 = pipe client identity
    /// rejected, 24 = protocol/response timeout.
    /// </summary>
    public static class SingleInstanceExitCodes
    {
        public const int Success = 0;
        public const int NoPrimary = 20;
        public const int PrimaryNotReady = 21;
        public const int ForegroundDenied = 22;
        public const int IdentityRejected = 23;
        public const int ProtocolTimeout = 24;

        public static string Describe(int code)
        {
            switch (code)
            {
                case Success: return "已唤醒已有实例。";
                case NoPrimary: return "没有正在运行的 PowerDock 主实例（互斥量被占用但管道未就绪）。";
                case PrimaryNotReady: return "主实例正在启动但尚未就绪，请稍后重试。";
                case ForegroundDenied: return "已找到主实例，但系统拒绝将其切到前台；已闪烁任务栏按钮提示。";
                case IdentityRejected: return "另一用户会话/进程的请求被拒绝（客户端身份校验失败）。";
                case ProtocolTimeout: return "与主实例通信超时，请重试。";
                default: return "未知结果（exit=" + code + "）。";
            }
        }
    }

    /// <summary>
    /// Single-instance coordinator: per-user + per-session mutex, a readiness event that is signaled
    /// only after the pipe server is listening, and a bounded request/response protocol.
    ///
    /// The secondary instance never guesses success: it waits for the readiness event, performs the
    /// directed AllowSetForegroundWindow(primaryPid) grant, sends "PID:&lt;pid&gt;", reads a
    /// "RESULT:&lt;outcome&gt;" line and maps the outcome to the exit-code contract above.
    /// </summary>
    public sealed class SingleInstanceService : IDisposable
    {
        private const int ReadyWaitMs = 1500;
        private const int ConnectWaitMs = 1500;
        private const int ProtocolTimeoutMs = 2500;
        private const int FeedbackDialogMs = 4000;

        private readonly string _mutexName;
        private readonly string _pipeName;
        private readonly string _readyEventName;

        private Mutex _mutex;
        private bool _ownsMutex;
        private bool _isFirstInstance;
        private volatile bool _disposed;

        private Func<int, ActivationResult> _onActivateReceived;
        private Thread _listenerThread;
        private ManualResetEvent _stopEvent;
        private EventWaitHandle _readyEvent;
        private NamedPipeServerStream _currentServerStream;
        private bool _listenerReady;

        public bool IsFirstInstance
        {
            get { return _isFirstInstance; }
        }

        public string MutexName
        {
            get { return _mutexName; }
        }

        public string PipeName
        {
            get { return _pipeName; }
        }

        public string ReadyEventName
        {
            get { return _readyEventName; }
        }

        /// <summary>The exit code the secondary instance must return (0 for the primary path).</summary>
        public int SecondaryExitCode { get; private set; }

        /// <summary>Human-readable reason for a non-zero secondary exit, written to the log.</summary>
        public string SecondaryFailureDetail { get; private set; }

        public SingleInstanceService()
            : this(GetDefaultMutexName(), GetDefaultPipeName())
        {
        }

        public SingleInstanceService(string mutexName, string pipeName)
            : this(mutexName, pipeName, pipeName + "_Ready")
        {
        }

        public SingleInstanceService(string mutexName, string pipeName, string readyEventName)
        {
            _mutexName = mutexName;
            _pipeName = pipeName;
            _readyEventName = readyEventName;
            _stopEvent = new ManualResetEvent(false);
            SecondaryExitCode = SingleInstanceExitCodes.Success;
        }

        public static string GetDefaultMutexName()
        {
            return "Local\\PowerDock_Mutex_" + GetSessionId() + "_" + GetCurrentUserSid();
        }

        public static string GetDefaultPipeName()
        {
            return "PowerDock_Pipe_" + GetSessionId() + "_" + GetCurrentUserSid();
        }

        public static string GetDefaultReadyEventName()
        {
            return "Local\\PowerDock_Ready_" + GetSessionId() + "_" + GetCurrentUserSid();
        }

        private static int GetSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return 0; }
        }

        private static string _currentUserSid;

        private static string GetCurrentUserSid()
        {
            if (!string.IsNullOrEmpty(_currentUserSid)) return _currentUserSid;
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity != null && identity.User != null)
                    {
                        _currentUserSid = identity.User.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Failed to resolve current user SID: " + ex.Message);
            }
            return _currentUserSid ?? "unknown";
        }

        /// <summary>
        /// Primary path: acquire ownership (WaitOne(0), not merely "createdNew"), start the pipe
        /// listener and signal readiness only after the pipe is actually listening. Secondary path:
        /// wait for readiness, perform the directed foreground grant and return a differentiated
        /// exit code instead of exiting 0 silently.
        /// </summary>
        public bool TryStartAsFirstInstance(Func<int, ActivationResult> onActivateReceived)
        {
            if (_disposed) throw new ObjectDisposedException("SingleInstanceService");
            _onActivateReceived = onActivateReceived;
            _isFirstInstance = false;
            SecondaryExitCode = SingleInstanceExitCodes.Success;

            bool createdReadyEvent = false;
            try
            {
                _readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _readyEventName, out createdReadyEvent);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to create single-instance readiness event: " + _readyEventName, ex);
                _readyEvent = null;
            }

            bool ownsMutex = false;
            try
            {
                bool createdNew;
                _mutex = new Mutex(false, _mutexName, out createdNew);
                ownsMutex = _mutex.WaitOne(0);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Warn("Cannot open single-instance mutex (access denied): " + ex.Message);
                ownsMutex = false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Single-instance mutex creation error: " + _mutexName, ex);
                ownsMutex = false;
            }

            if (ownsMutex)
            {
                _ownsMutex = true;
                _isFirstInstance = true;
                AppLogger.Info("Acquired single-instance mutex as primary instance: " + _mutexName);
                StartPipeServerListener();
                return true;
            }

            // Secondary path: do not keep a mutex handle and do not leave a readiness event behind
            // when this process created one but another (foreign) process owns the mutex.
            if (_mutex != null)
            {
                try { _mutex.Close(); } catch { }
                _mutex = null;
            }
            if (_readyEvent != null)
            {
                try { _readyEvent.Close(); } catch { }
                _readyEvent = null;
            }
            if (createdReadyEvent)
            {
                // Best effort: if we created the named event and nobody else holds it, closing the
                // handle above already removed it; nothing else to do.
            }

            AppLogger.Info("Mutex is already held by primary instance. Notifying primary instance via pipe: " + _pipeName);
            SecondaryExitCode = NotifyPrimaryInstance(ReadyWaitMs, ConnectWaitMs);
            return false;
        }

        private int NotifyPrimaryInstance(int readyWaitMs, int connectWaitMs)
        {
            // 1) Distinct "no primary" (20) from "primary is starting" (21) using the readiness event.
            EventWaitHandle ready = null;
            Stopwatch readyWatch = Stopwatch.StartNew();
            while (ready == null && readyWatch.ElapsedMilliseconds < readyWaitMs)
            {
                try
                {
                    ready = EventWaitHandle.OpenExisting(_readyEventName);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    SecondaryFailureDetail = "Failed to open readiness event '" + _readyEventName + "': " + ex.Message;
                    AppLogger.Warn(SecondaryFailureDetail);
                    return SingleInstanceExitCodes.NoPrimary;
                }
            }

            if (ready == null)
            {
                SecondaryFailureDetail = "No primary instance: mutex is held but readiness event '" + _readyEventName + "' is absent after " + readyWaitMs + "ms";
                AppLogger.Warn(SecondaryFailureDetail);
                return SingleInstanceExitCodes.NoPrimary;
            }

            using (ready)
            {
                if (!ready.WaitOne(readyWaitMs))
                {
                    SecondaryFailureDetail = "Primary instance is starting but its pipe is not ready within " + readyWaitMs + "ms";
                    AppLogger.Warn(SecondaryFailureDetail);
                    return SingleInstanceExitCodes.PrimaryNotReady;
                }
            }

            // 2) Connect with a bounded retry budget.
            NamedPipeClientStream client = null;
            try
            {
                client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                Stopwatch connectWatch = Stopwatch.StartNew();
                bool connected = false;
                while (!connected && connectWatch.ElapsedMilliseconds < connectWaitMs)
                {
                    try
                    {
                        int remaining = connectWaitMs - (int)connectWatch.ElapsedMilliseconds;
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
                    SecondaryFailureDetail = "Primary instance signaled ready but the pipe did not accept a connection within " + connectWaitMs + "ms";
                    AppLogger.Warn("Failed to connect to primary instance named pipe within timeout: " + connectWaitMs + "ms");
                    return SingleInstanceExitCodes.NoPrimary;
                }

                // 3) Directed foreground grant to the *primary* PID (never ASFW_ANY), with the
                //    outcome logged explicitly so "authorization failed" is distinguishable from
                //    "the primary did not respond".
                uint primaryPid;
                if (NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out primaryPid) && primaryPid != 0)
                {
                    bool authorized = NativeMethods.AllowSetForegroundWindow((int)primaryPid);
                    int authError = Marshal.GetLastWin32Error();
                    AppLogger.Info(string.Format(
                        "Foreground authorization: AllowSetForegroundWindow({0}) returned {1}, win32={2}",
                        primaryPid, authorized, authError));
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    AppLogger.Warn("Foreground authorization: GetNamedPipeServerProcessId failed, win32=" + err);
                }

                // 4) Bounded request/response.
                StreamReader reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
                StreamWriter writer = new StreamWriter(client, Encoding.UTF8, 4096, true);
                writer.AutoFlush = true;
                int currentPid = Process.GetCurrentProcess().Id;
                if (!WriteLineWithTimeout(writer, "PID:" + currentPid, 1000))
                {
                    SecondaryFailureDetail = "Protocol timeout: failed to send the activate request";
                    AppLogger.Error(SecondaryFailureDetail);
                    return SingleInstanceExitCodes.ProtocolTimeout;
                }

                string response = ReadLineWithTimeout(reader, ProtocolTimeoutMs);
                if (string.IsNullOrEmpty(response))
                {
                    SecondaryFailureDetail = "Protocol timeout: primary did not answer within " + ProtocolTimeoutMs + "ms";
                    AppLogger.Error(SecondaryFailureDetail);
                    return SingleInstanceExitCodes.ProtocolTimeout;
                }

                response = response.Trim();
                AppLogger.Info("Primary instance response: " + response);
                if (!response.StartsWith("RESULT:", StringComparison.OrdinalIgnoreCase))
                {
                    SecondaryFailureDetail = "Protocol error: unexpected primary response '" + response + "'";
                    AppLogger.Error(SecondaryFailureDetail);
                    return SingleInstanceExitCodes.ProtocolTimeout;
                }

                string outcome = response.Substring("RESULT:".Length).Trim().ToUpperInvariant();
                switch (outcome)
                {
                    case "ACTIVATED":
                        SecondaryFailureDetail = null;
                        return SingleInstanceExitCodes.Success;
                    case "DENIED":
                    case "NOWINDOW":
                        SecondaryFailureDetail = "Primary instance reported activation failure: " + outcome;
                        AppLogger.Warn(SecondaryFailureDetail);
                        return SingleInstanceExitCodes.ForegroundDenied;
                    case "IDENTITYREJECTED":
                    case "IDENTITY_REJECTED":
                        SecondaryFailureDetail = "Primary instance rejected the client identity";
                        AppLogger.Warn(SecondaryFailureDetail);
                        return SingleInstanceExitCodes.IdentityRejected;
                    default:
                        SecondaryFailureDetail = "Protocol error: unknown activation outcome '" + outcome + "'";
                        AppLogger.Error(SecondaryFailureDetail);
                        return SingleInstanceExitCodes.ProtocolTimeout;
                }
            }
            catch (Exception ex)
            {
                SecondaryFailureDetail = "Exception while notifying the primary instance: " + ex.Message;
                AppLogger.Error("Exception in NotifyPrimaryInstance", ex);
                return SingleInstanceExitCodes.ProtocolTimeout;
            }
            finally
            {
                if (client != null)
                {
                    try { client.Dispose(); } catch { }
                }
            }
        }

        private static string ReadLineWithTimeout(StreamReader reader, int timeoutMs)
        {
            try
            {
                return reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool WriteLineWithTimeout(StreamWriter writer, string line, int timeoutMs)
        {
            try
            {
                writer.WriteLineAsync(line).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Brings the main window to the foreground. Always logs the SetForegroundWindow outcome so
        /// the deny path is observable; falls back to a bounded taskbar flash when the system refuses.
        /// </summary>
        public bool ActivateWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                AppLogger.Warn("Foreground activation: no window handle to activate");
                return false;
            }

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
                AppLogger.Info("Foreground activation: SetForegroundWindow returned " + broughtToForeground);
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
                    return false;
                }

                AppLogger.Info("Window brought to foreground successfully.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error activating window: " + hWnd, ex);
                return false;
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
                    string aclReason;
                    PipeSecurity security = CreatePipeSecurity(out aclReason);
                    if (security != null)
                    {
                        server = NamedPipeServerStreamAcl.Create(
                            _pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous,
                            4096,
                            4096,
                            security);
                    }
                    else
                    {
                        AppLogger.Warn("PipeSecurity unavailable (" + aclReason + "); falling back to the default pipe security descriptor.");
                        server = new NamedPipeServerStream(
                            _pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);
                    }

                    _currentServerStream = server;
                    IAsyncResult waitHandle = server.BeginWaitForConnection(null, null);

                    if (!_listenerReady)
                    {
                        _listenerReady = true;
                        try { if (_readyEvent != null) _readyEvent.Set(); } catch { }
                        AppLogger.Info("Primary instance ready (pipe listening): " + _pipeName);
                    }

                    bool stopRequested = false;
                    while (true)
                    {
                        int waitIndex = WaitHandle.WaitAny(new WaitHandle[] { waitHandle.AsyncWaitHandle, _stopEvent }, 250);
                        if (waitIndex == 1 || _disposed)
                        {
                            stopRequested = true;
                            break;
                        }
                        if (waitIndex == 0)
                        {
                            break;
                        }
                    }

                    if (stopRequested) break;

                    server.EndWaitForConnection(waitHandle);
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
            int clientPid;
            string rejectReason;
            if (!ValidateClientIdentity(server, out clientPid, out rejectReason))
            {
                AppLogger.Warn("Rejected pipe client: " + rejectReason);
                TrySendResult(server, ActivationResult.IdentityRejected);
                return;
            }

            try
            {
                StreamReader reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
                string request = ReadLineWithTimeout(reader, ProtocolTimeoutMs);
                if (string.IsNullOrEmpty(request))
                {
                    AppLogger.Warn("Pipe client " + clientPid + " sent no request within " + ProtocolTimeoutMs + "ms; dropping connection.");
                    return;
                }

                string requestPid = request.Trim();
                if (requestPid.StartsWith("PID:", StringComparison.OrdinalIgnoreCase))
                {
                    requestPid = requestPid.Substring(4).Trim();
                }
                AppLogger.Info("Received activate request from client PID: " + requestPid);

                ActivationResult result = ExecuteOnUIThread(clientPid, ProtocolTimeoutMs);
                TrySendResult(server, result);
                AppLogger.Info("Primary instance response: " + result.ToString().ToUpperInvariant());
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Error handling pipe client connection: " + ex.Message);
            }
        }

        private static void TrySendResult(NamedPipeServerStream server, ActivationResult result)
        {
            try
            {
                StreamWriter writer = new StreamWriter(server, Encoding.UTF8, 4096, true);
                writer.AutoFlush = true;
                WriteLineWithTimeout(writer, "RESULT:" + result.ToString().ToUpperInvariant(), 1000);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Failed to send activation result: " + ex.Message);
            }
        }

        private static bool ValidateClientIdentity(NamedPipeServerStream server, out int clientPid, out string reason)
        {
            clientPid = 0;
            reason = null;
            try
            {
                uint pid;
                if (!NativeMethods.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out pid) || pid == 0)
                {
                    reason = "GetNamedPipeClientProcessId failed, win32=" + Marshal.GetLastWin32Error();
                    return false;
                }
                clientPid = (int)pid;

                string clientSid;
                int sidError;
                if (!NativeMethods.TryGetProcessUserSid(clientPid, out clientSid, out sidError))
                {
                    reason = "token SID lookup failed for pid " + clientPid + ", win32=" + sidError;
                    return false;
                }

                string currentSid = GetCurrentUserSid();
                if (!string.Equals(clientSid, currentSid, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "client SID " + clientSid + " != current user SID " + currentSid + " (pid " + clientPid + ")";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "identity validation exception: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static PipeSecurity CreatePipeSecurity(out string reason)
        {
            reason = null;
            try
            {
                PipeSecurity security = new PipeSecurity();
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity != null && identity.User != null)
                    {
                        security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                        return security;
                    }
                    reason = "current Windows identity has no user SID";
                }
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
            }
            return null;
        }

        private ActivationResult ExecuteOnUIThread(int clientPid, int timeoutMs)
        {
            Func<ActivationResult> invoke = delegate
            {
                Func<int, ActivationResult> callback = _onActivateReceived;
                return callback != null ? callback(clientPid) : ActivationResult.NoWindow;
            };

            Application app = Application.Current;
            if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            {
                return invoke();
            }
            if (app.Dispatcher.CheckAccess())
            {
                return invoke();
            }

            try
            {
                Task<ActivationResult> task = Task.Run(delegate
                {
                    return app.Dispatcher.Invoke(invoke, System.Windows.Threading.DispatcherPriority.Send);
                });
                if (task.Wait(timeoutMs))
                {
                    return task.Result;
                }
                AppLogger.Warn("Activation callback timed out on the UI thread after " + timeoutMs + "ms.");
                return ActivationResult.ProtocolTimeout;
            }
            catch (AggregateException ex)
            {
                AppLogger.Error("Activation callback failed on the UI thread", ex.InnerException ?? ex);
                return ActivationResult.ProtocolTimeout;
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

                if (_readyEvent != null)
                {
                    try { _readyEvent.Close(); } catch { }
                    _readyEvent = null;
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
