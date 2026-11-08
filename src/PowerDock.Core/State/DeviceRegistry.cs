using System;
using System.Collections.Generic;
using PowerDock.Core.Common;
using PowerDock.Core.State;

namespace PowerDock.Core.State
{
    /// <summary>
    /// 管理「读到过哪些设备」。只按 DeviceKey 归并：同一物理设备的多个接口/来源挂到同一条记录，
    /// 不同 DeviceKey 永不因名称、型号或 VID/PID 相同而合并（D17）。
    /// 线程安全：采集线程写入，UI 线程读快照，全部经 <see cref="Sync"/> 保护。
    /// </summary>
    public sealed class DeviceRegistry
    {
        public sealed class Record
        {
            public DeviceKey Key { get; private set; }
            public DeviceIdentity Identity { get; set; }
            public LinkInfo Link { get; set; }
            public string DisplayName { get; set; }
            public DeviceCategory Category { get; set; }
            public Dictionary<ReadingKey, SourceReading> Readings { get; private set; }
            public bool Removed { get; set; }

            public Record(DeviceKey key)
            {
                Key = key;
                Readings = new Dictionary<ReadingKey, SourceReading>();
            }
        }

        private readonly Dictionary<DeviceKey, Record> _records = new Dictionary<DeviceKey, Record>();

        public Record GetOrAdd(DeviceIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException("identity");

            Record record;
            if (!_records.TryGetValue(identity.Key, out record))
            {
                record = new Record(identity.Key);
                _records.Add(identity.Key, record);
            }

            record.Identity = identity;
            record.Removed = false;
            return record;
        }

        public bool TryGet(DeviceKey key, out Record record)
        {
            return _records.TryGetValue(key, out record);
        }

        public void Remove(DeviceKey key)
        {
            _records.Remove(key);
        }

        public IList<Record> All()
        {
            return new List<Record>(_records.Values);
        }

        public int Count
        {
            get { return _records.Count; }
        }
    }
}
