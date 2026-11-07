using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PowerDock.App.Interop;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Interop
{
    /// <summary>
    /// Owns the main-window DWM backdrop lifecycle:
    ///   1. CompositionTarget.BackgroundColor = Transparent (WPF's HwndTarget otherwise paints an
    ///      opaque target and DWM has nothing to show through — root cause of the 0% transmission);
    ///   2. checked frame extension + attribute HRESULTs + read-back;
    ///   3. staged activation: backdrop NONE first, Acrylic(3) on a later compositor frame;
    ///   4. redo on ContentRendered / IsVisibleChanged / composition, theme and settings changes —
    ///      and on every show (§7.3);
    ///   5. explicit static fallback when DWM composition / system transparency / high contrast /
    ///      AllowsTransparency makes the material unavailable.
    /// The α235 surface colour is NOT changed here; the fallback only replaces the surface brush
    /// with an opaque version of the same RGB while the material is unavailable.
    /// </summary>
    public sealed class WindowBackdropController : IDisposable
    {
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_THEMECHANGED = 0x031A;
        private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
        private const int StagedActivationDelayMs = 250;

        private static readonly Color SurfaceRgb = Color.FromRgb(20, 22, 28); // #14161C
        private static readonly Brush MaterialSurfaceBrush = new SolidColorBrush(Color.FromArgb(235, 20, 22, 28));
        private static readonly Brush FallbackSurfaceBrush = new SolidColorBrush(SurfaceRgb);

        private Window _window;
        private Border _surface;
        private HwndSource _source;
        private IntPtr _hwnd = IntPtr.Zero;
        private DispatcherTimer _stagedTimer;
        private bool _disposed;
        private readonly List<string> _events = new List<string>();

        public WindowBackdropController()
        {
            ForceFallback = false;
        }

        /// <summary>Diagnostic-only: skip material and apply the explicit static fallback.</summary>
        public bool ForceFallback { get; set; }

        public bool CompositionEnabled { get; private set; }
        public bool SystemTransparencyEnabled { get; private set; }
        public bool HighContrast { get; private set; }
        public bool AllowsTransparency { get; private set; }
        public bool SupportsBackdropApi { get; private set; }
        public bool CompositionTargetTransparent { get; private set; }

        public int CornerHr { get; private set; }
        public int CornerReadback { get; private set; }
        public int DarkHr { get; private set; }
        public int DarkReadback { get; private set; }
        public int ExtendFrameHr { get; private set; }
        public int NoneHr { get; private set; }
        public int NoneReadback { get; private set; }
        public int AcrylicHr { get; private set; }
        public int AcrylicReadback { get; private set; }
        public bool FrameChangedCallOk { get; private set; }
        public bool StagedActivationRequested { get; private set; }
        public bool StagedActivationLogged { get; private set; }

        public bool MaterialReady { get; private set; }
        public bool FallbackActive { get; private set; }
        public string LastReason { get; private set; }
        public int RebindCount { get; private set; }

        public IList<string> Events
        {
            get { return _events.AsReadOnly(); }
        }

        public void Attach(Window window, Border surface)
        {
            if (window == null) throw new ArgumentNullException("window");
            _window = window;
            _surface = surface;
            AllowsTransparency = window.AllowsTransparency;

            window.SourceInitialized += OnSourceInitialized;
            window.ContentRendered += OnContentRendered;
            window.IsVisibleChanged += OnIsVisibleChanged;
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var helper = new WindowInteropHelper(_window);
                _hwnd = helper.Handle;
                _source = HwndSource.FromHwnd(_hwnd);
                if (_source != null)
                {
                    _source.AddHook(WindowMessage);
                    if (_source.CompositionTarget != null)
                    {
                        // Root-cause fix: without this the WPF composition target is opaque.
                        _source.CompositionTarget.BackgroundColor = Colors.Transparent;
                        CompositionTargetTransparent = _source.CompositionTarget.BackgroundColor == Colors.Transparent;
                    }
                }

                CompositionEnabled = DwmHelper.IsCompositionEnabled();
                SystemTransparencyEnabled = DwmHelper.IsSystemTransparencyEnabled();
                HighContrast = DwmHelper.IsHighContrast;
                SupportsBackdropApi = DwmHelper.SupportsAcrylicBackdrop();

                LogEvent(string.Format("capabilities composition={0} systemTransparency={1} highContrast={2} allowsTransparency={3} supportsBackdropApi={4} compositionTargetTransparent={5}",
                    CompositionEnabled, SystemTransparencyEnabled, HighContrast, AllowsTransparency, SupportsBackdropApi, CompositionTargetTransparent));

                Apply("SourceInitialized");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Backdrop: SourceInitialized failed", ex);
                SetFallback("exception in SourceInitialized: " + ex.Message);
            }
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            ScheduleRebind("ContentRendered");
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_window != null && _window.IsVisible)
            {
                // §7.3: every show must redo the frame extension, not only the first Loaded pass.
                ScheduleRebind("IsVisibleChanged(visible)");
            }
            else
            {
                if (_stagedTimer != null) _stagedTimer.Stop();
                LogEvent("rebind skipped: window hidden");
            }
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == WM_DWMCOMPOSITIONCHANGED || message == WM_THEMECHANGED || message == WM_SETTINGCHANGE)
            {
                ScheduleRebind("WM message 0x" + message.ToString("X4"));
            }
            return IntPtr.Zero;
        }

        private void ScheduleRebind(string reason)
        {
            if (_disposed || _window == null) return;
            LogEvent("rebind scheduled: " + reason);
            _window.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!_disposed && _window != null && _window.IsVisible)
                {
                    Apply(reason);
                }
            }), DispatcherPriority.Loaded);
        }

        private void Apply(string reason)
        {
            if (_disposed || _window == null) return;
            RebindCount++;
            LastReason = reason;
            if (_stagedTimer != null) { _stagedTimer.Stop(); _stagedTimer = null; }

            if (!_window.IsVisible)
            {
                LogEvent("apply skipped (hidden): " + reason);
                return;
            }

            string fallbackReason = null;
            if (AllowsTransparency)
            {
                fallbackReason = "window.AllowsTransparency=true disables the DWM backdrop path";
            }
            else if (ForceFallback)
            {
                fallbackReason = "diagnostic ForceFallback";
            }
            else if (!CompositionEnabled)
            {
                fallbackReason = "DWM composition disabled";
            }
            else if (!SystemTransparencyEnabled)
            {
                fallbackReason = "system transparency disabled (EnableTransparency=0)";
            }
            else if (HighContrast)
            {
                fallbackReason = "high contrast mode";
            }
            else if (!SupportsBackdropApi)
            {
                fallbackReason = "OS build below 22621 (DWMWA_SYSTEMBACKDROP_TYPE unsupported)";
            }
            else if (_hwnd == IntPtr.Zero)
            {
                fallbackReason = "no HWND";
            }
            else if (!CompositionTargetTransparent)
            {
                fallbackReason = "CompositionTarget background is not transparent";
            }

            if (fallbackReason != null)
            {
                SetFallback(fallbackReason);
                return;
            }

            ApplyChrome();
            ExtendFrameHr = DwmHelper.ExtendFrame(_hwnd);
            FrameChangedCallOk = DwmHelper.ForceFrameChanged(_hwnd);
            LogEvent("apply[" + reason + "] ExtendFrame(-1) hr=" + DwmHelper.HResultText(ExtendFrameHr) + " ForceFrameChanged=" + FrameChangedCallOk);
            if (ExtendFrameHr != 0)
            {
                SetFallback("DwmExtendFrameIntoClientArea failed hr=" + DwmHelper.HResultText(ExtendFrameHr));
                return;
            }

            int noneReadback;
            NoneHr = DwmHelper.SetBackdropType(_hwnd, DwmHelper.DWMSBT_NONE, out noneReadback);
            NoneReadback = noneReadback;
            LogEvent("apply[" + reason + "] backdrop NONE(1) hr=" + DwmHelper.HResultText(NoneHr) + " readback=" + NoneReadback);
            if (NoneHr != 0)
            {
                SetFallback("DwmSetWindowAttribute(NONE) failed hr=" + DwmHelper.HResultText(NoneHr));
                return;
            }

            // Keep the surface opaque until the material is actually read back, so a failed or
            // delayed activation never shows as a grey plate.
            SetSurface(false);
            MaterialReady = false;
            FallbackActive = false;

            _stagedTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, _window.Dispatcher);
            _stagedTimer.Interval = TimeSpan.FromMilliseconds(StagedActivationDelayMs);
            _stagedTimer.Tick += OnStagedActivation;
            _stagedTimer.Start();
            StagedActivationRequested = true;
            LogEvent("apply[" + reason + "] staged Acrylic(3) scheduled after " + StagedActivationDelayMs + "ms (separate compositor frame)");
        }

        private void ApplyChrome()
        {
            int cornerReadback;
            CornerHr = DwmHelper.SetCornerPreference(_hwnd, DwmHelper.DWMWCP_ROUND, out cornerReadback);
            CornerReadback = cornerReadback;
            int darkReadback;
            DarkHr = DwmHelper.SetDarkMode(_hwnd, true, out darkReadback);
            DarkReadback = darkReadback;
            LogEvent("apply[" + LastReason + "] corner hr=" + DwmHelper.HResultText(CornerHr) + " readback=" + CornerReadback +
                     " dark hr=" + DwmHelper.HResultText(DarkHr) + " readback=" + DarkReadback);
        }

        private void OnStagedActivation(object sender, EventArgs e)
        {
            if (_stagedTimer != null) { _stagedTimer.Stop(); _stagedTimer = null; }
            if (_disposed || _window == null || !_window.IsVisible) return;

            int acrylicReadback;
            AcrylicHr = DwmHelper.SetBackdropType(_hwnd, DwmHelper.DWMSBT_TRANSIENTWINDOW, out acrylicReadback);
            AcrylicReadback = acrylicReadback;
            StagedActivationLogged = true;
            LogEvent("staged acrylic hr=" + DwmHelper.HResultText(AcrylicHr) + " readback=" + AcrylicReadback);
            if (AcrylicHr != 0 || AcrylicReadback != DwmHelper.DWMSBT_TRANSIENTWINDOW)
            {
                SetFallback("acrylic activation failed (hr=" + DwmHelper.HResultText(AcrylicHr) + ", readback=" + AcrylicReadback + ")");
                return;
            }

            MaterialReady = true;
            FallbackActive = false;
            SetSurface(true);
            LogEvent("MATERIAL READY reason=" + LastReason +
                     " alpha235 expected transmission=0.0784 (backdrop255 -> interior luma ~38.43 vs current baseline 20.01)");
        }

        private void SetFallback(string reason)
        {
            MaterialReady = false;
            FallbackActive = true;
            LastReason = reason;
            SetSurface(false);
            LogEvent("FALLBACK static reason=" + reason + " surface=opaque#14161C");
        }

        private void SetSurface(bool materialReady)
        {
            if (_surface == null) return;
            _surface.Background = materialReady ? MaterialSurfaceBrush : FallbackSurfaceBrush;
            LogEvent("surface=" + (materialReady ? "alpha235(material)" : "opaque(fallback)"));
        }

        private void LogEvent(string message)
        {
            _events.Add(message);
            AppLogger.Info("Backdrop: " + message);
        }

        public string SnapshotJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
            sb.AppendLine("  \"compositionEnabled\": " + (CompositionEnabled ? "true" : "false") + ",");
            sb.AppendLine("  \"systemTransparencyEnabled\": " + (SystemTransparencyEnabled ? "true" : "false") + ",");
            sb.AppendLine("  \"highContrast\": " + (HighContrast ? "true" : "false") + ",");
            sb.AppendLine("  \"allowsTransparency\": " + (AllowsTransparency ? "true" : "false") + ",");
            sb.AppendLine("  \"supportsBackdropApi\": " + (SupportsBackdropApi ? "true" : "false") + ",");
            sb.AppendLine("  \"compositionTargetTransparent\": " + (CompositionTargetTransparent ? "true" : "false") + ",");
            sb.AppendLine("  \"cornerHr\": " + CornerHr + ", \"cornerReadback\": " + CornerReadback + ",");
            sb.AppendLine("  \"darkHr\": " + DarkHr + ", \"darkReadback\": " + DarkReadback + ",");
            sb.AppendLine("  \"extendFrameHr\": " + ExtendFrameHr + ", \"frameChangedCallOk\": " + (FrameChangedCallOk ? "true" : "false") + ",");
            sb.AppendLine("  \"noneHr\": " + NoneHr + ", \"noneReadback\": " + NoneReadback + ",");
            sb.AppendLine("  \"acrylicHr\": " + AcrylicHr + ", \"acrylicReadback\": " + AcrylicReadback + ",");
            sb.AppendLine("  \"stagedActivationRequested\": " + (StagedActivationRequested ? "true" : "false") + ",");
            sb.AppendLine("  \"materialReady\": " + (MaterialReady ? "true" : "false") + ",");
            sb.AppendLine("  \"fallbackActive\": " + (FallbackActive ? "true" : "false") + ",");
            sb.AppendLine("  \"rebindCount\": " + RebindCount + ",");
            sb.AppendLine("  \"lastReason\": \"" + Escape(LastReason) + "\",");
            sb.AppendLine("  \"events\": [");
            for (int i = 0; i < _events.Count; i++)
            {
                sb.Append("    \"" + Escape(_events[i]) + "\"");
                if (i < _events.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_stagedTimer != null) { try { _stagedTimer.Stop(); } catch { } _stagedTimer = null; }
            if (_source != null) { try { _source.RemoveHook(WindowMessage); } catch { } _source = null; }
            if (_window != null)
            {
                try
                {
                    _window.SourceInitialized -= OnSourceInitialized;
                    _window.ContentRendered -= OnContentRendered;
                    _window.IsVisibleChanged -= OnIsVisibleChanged;
                    _window.Closed -= OnClosed;
                }
                catch { }
                _window = null;
            }
        }
    }
}

