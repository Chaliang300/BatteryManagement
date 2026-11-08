using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Providers;
using PowerDock.Core.State;

namespace PowerDock.Core.Running
{
    /// <summary>
    /// 采集运行时：把真实 Provider 装进 DeviceStateService，并按 D9 的周期做兜底刷新。
    /// 约束：
    /// 1. 只在这里组装 Provider，UI 层不认识传输细节；
    /// 2. 手动刷新走 <see cref="RefreshNowAsync"/>，与定时刷新共用同一入口；
    /// 3. 启动顺序按 AGENTS §11：本机电池 → HID → 蓝牙，互不阻塞；
    /// 4. 任一 Provider 失败只记日志，不影响其他来源与已缓存读数。
    /// </summary>
    public sealed class AcquisitionRuntime : IDisposable
    {
        private readonly DeviceStateService _service;
        private readonly List<IDeviceProvider> _providers = new List<IDeviceProvider>();
        private readonly object _refreshSync = new object();
        private Timer _timer;
        private bool _refreshing;
        private bool _disposed;

        public AcquisitionRuntime(DeviceStateService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            _service = service;
        }

        public DeviceStateService Service
        {
            get { return _service; }
        }

        public IReadOnlyList<IDeviceProvider> Providers
        {
            get { return _providers; }
        }

        public void Add(IDeviceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            _providers.Add(provider);
            _service.Attach(provider);
        }

        /// <summary>启动采集：先各自发现一次，再按 fallbackInterval 做兜底刷新。</summary>
        public async Task StartAsync(TimeSpan fallbackInterval, CancellationToken ct)
        {
            foreach (IDeviceProvider provider in _providers.ToArray())
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await provider.DiscoverAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Provider discovery failed: " + provider.ProviderId, ex);
                }
            }

            _service.PublishNow();

            if (fallbackInterval > TimeSpan.Zero)
            {
                _timer = new Timer(OnTick, null, fallbackInterval, fallbackInterval);
            }
        }

        private void OnTick(object state)
        {
            try
            {
                RefreshNowAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Scheduled refresh failed", ex);
            }
        }

        /// <summary>
        /// 立即刷新全部读端点。同一时刻只允许一次全量刷新，重复调用直接返回，避免手动连点叠加 GATT 会话。
        /// </summary>
        public async Task RefreshNowAsync(CancellationToken ct)
        {
            lock (_refreshSync)
            {
                if (_refreshing || _disposed) return;
                _refreshing = true;
            }

            try
            {
                await _service.RefreshAllAsync(ct).ConfigureAwait(false);
                _service.PublishNow();
            }
            finally
            {
                lock (_refreshSync)
                {
                    _refreshing = false;
                }
            }
        }

        public void Dispose()
        {
            Timer timer;
            lock (_refreshSync)
            {
                if (_disposed) return;
                _disposed = true;
                timer = _timer;
                _timer = null;
            }

            if (timer != null) timer.Dispose();
            _service.Dispose();
        }
    }
}
