using System;
using System.Collections.Generic;

namespace PowerDock.Core.Widgets
{
    /// <summary>
    /// 挂件全量展示模型（纯数据，供预览控件及桌面宿主复用）。
    /// </summary>
    public sealed class WidgetPresentationModel
    {
        public WidgetSizeKind SizeKind { get; }
        public double WidthDip { get; }
        public double HeightDip { get; }
        public double CornerRadiusDip { get; }
        public double PaddingDip { get; }
        public int MaxRows { get; }

        public int TotalDeviceCount { get; }
        public int OnlineDeviceCount { get; }
        public int SelectedCount { get; }

        public bool HasDevices => TotalDeviceCount > 0;
        public bool HasSelectedDevices => SelectedCount > 0;

        public IReadOnlyList<WidgetRowItem> DisplayedRows { get; }
        public string Footnote { get; }

        /// <summary>
        /// 是否已连接桌面宿主（AGENTS.md §6.5：宿主尚未实现时，明确标为预览）。
        /// </summary>
        public bool IsHostConnected { get; }

        public WidgetPresentationModel(
            WidgetSizeKind sizeKind,
            IReadOnlyList<WidgetRowItem> displayedRows,
            int totalDeviceCount,
            int onlineDeviceCount,
            int selectedCount,
            string footnote,
            bool isHostConnected = false)
        {
            SizeKind = sizeKind;
            WidthDip = WidgetSizeSpecs.GetWidth(sizeKind);
            HeightDip = WidgetSizeSpecs.GetHeight(sizeKind);
            CornerRadiusDip = WidgetSizeSpecs.GetCornerRadius(sizeKind);
            PaddingDip = WidgetSizeSpecs.GetPadding(sizeKind);
            MaxRows = WidgetSizeSpecs.GetMaxRows(sizeKind);

            DisplayedRows = displayedRows ?? Array.Empty<WidgetRowItem>();
            TotalDeviceCount = totalDeviceCount;
            OnlineDeviceCount = onlineDeviceCount;
            SelectedCount = selectedCount;
            Footnote = footnote ?? string.Empty;
            IsHostConnected = isHostConnected;
        }
    }
}
