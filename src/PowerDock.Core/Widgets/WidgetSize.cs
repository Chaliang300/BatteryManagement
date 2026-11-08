using System;

namespace PowerDock.Core.Widgets
{
    /// <summary>
    /// 桌面挂件三档尺寸枚举（AGENTS.md §2.1 C1 / D10 / §6.3）。
    /// </summary>
    public enum WidgetSizeKind
    {
        Small2x2 = 0,
        Medium4x2 = 1,
        Large4x4 = 2
    }

    /// <summary>
    /// 挂件尺寸几何与规格契约。
    /// 严格遵循 AGENTS.md §6.3 裁决：
    /// - 小 2×2: 173×173 DIP, r22, pad14, 最多 3 行；
    /// - 中 4×2: 362×173 DIP, r27, pad16, 最多 5 行；
    /// - 大 4×4: 362×362 DIP, r27, pad22, 最多 7 行 + 汇总脚注。
    /// </summary>
    public static class WidgetSizeSpecs
    {
        public const double SmallWidthDip = 173.0;
        public const double SmallHeightDip = 173.0;
        public const double SmallCornerRadiusDip = 22.0;
        public const double SmallPaddingDip = 14.0;
        public const int SmallMaxRows = 3;

        public const double MediumWidthDip = 362.0;
        public const double MediumHeightDip = 173.0;
        public const double MediumCornerRadiusDip = 27.0;
        public const double MediumPaddingDip = 16.0;
        public const int MediumMaxRows = 5;

        public const double LargeWidthDip = 362.0;
        public const double LargeHeightDip = 362.0;
        public const double LargeCornerRadiusDip = 27.0;
        public const double LargePaddingDip = 22.0;
        public const int LargeMaxRows = 7;

        public static double GetWidth(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return SmallWidthDip;
                case WidgetSizeKind.Large4x4: return LargeWidthDip;
                case WidgetSizeKind.Medium4x2:
                default: return MediumWidthDip;
            }
        }

        public static double GetHeight(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return SmallHeightDip;
                case WidgetSizeKind.Large4x4: return LargeHeightDip;
                case WidgetSizeKind.Medium4x2:
                default: return MediumHeightDip;
            }
        }

        public static double GetCornerRadius(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return SmallCornerRadiusDip;
                case WidgetSizeKind.Large4x4: return LargeCornerRadiusDip;
                case WidgetSizeKind.Medium4x2:
                default: return MediumCornerRadiusDip;
            }
        }

        public static double GetPadding(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return SmallPaddingDip;
                case WidgetSizeKind.Large4x4: return LargePaddingDip;
                case WidgetSizeKind.Medium4x2:
                default: return MediumPaddingDip;
            }
        }

        public static int GetMaxRows(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return SmallMaxRows;
                case WidgetSizeKind.Large4x4: return LargeMaxRows;
                case WidgetSizeKind.Medium4x2:
                default: return MediumMaxRows;
            }
        }

        public static string GetCode(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return "small";
                case WidgetSizeKind.Large4x4: return "large";
                case WidgetSizeKind.Medium4x2:
                default: return "medium";
            }
        }

        public static string GetDisplayName(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return "小 2×2";
                case WidgetSizeKind.Large4x4: return "大 4×4";
                case WidgetSizeKind.Medium4x2:
                default: return "中 4×2";
            }
        }

        public static string GetDimensionText(WidgetSizeKind size)
        {
            switch (size)
            {
                case WidgetSizeKind.Small2x2: return "173×173";
                case WidgetSizeKind.Large4x4: return "362×362";
                case WidgetSizeKind.Medium4x2:
                default: return "362×173";
            }
        }

        public static WidgetSizeKind Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return WidgetSizeKind.Medium4x2;
            string trimmed = raw.Trim().ToLowerInvariant();
            if (trimmed == "small" || trimmed == "sm" || trimmed == "2x2" || trimmed == "2*2")
            {
                return WidgetSizeKind.Small2x2;
            }
            if (trimmed == "large" || trimmed == "lg" || trimmed == "4x4" || trimmed == "4*4")
            {
                return WidgetSizeKind.Large4x4;
            }
            return WidgetSizeKind.Medium4x2;
        }
    }
}
