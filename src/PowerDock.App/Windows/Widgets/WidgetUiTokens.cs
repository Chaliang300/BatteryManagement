using System;
using System.Windows.Media;
using PowerDock.Core.Widgets;

namespace PowerDock.App.Windows.Widgets
{
    /// <summary>
    /// 组件页与挂件视觉 Token 与冻结画刷（AGENTS.md §6.1 / mockup）。
    /// </summary>
    public static class WidgetUiTokens
    {
        public static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(107, 166, 255))); // #6BA6FF
        public static readonly Brush AccentSoftBrush = Freeze(new SolidColorBrush(Color.FromArgb(41, 107, 166, 255)));
        public static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(52, 211, 153)));     // #34D399
        public static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 159, 10)));   // #FF9F0A
        public static readonly Brush DangerBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 107, 107)));// #FF6B6B
        public static readonly Brush OffBrush = Freeze(new SolidColorBrush(Color.FromRgb(138, 143, 153)));   // #8A8F99

        public static readonly Brush Text1Brush = Freeze(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
        public static readonly Brush Text2Brush = Freeze(new SolidColorBrush(Color.FromArgb(173, 255, 255, 255)));
        public static readonly Brush Text3Brush = Freeze(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)));
        public static readonly Brush Text4Brush = Freeze(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)));

        public static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)));
        public static readonly Brush DefaultFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)));
        public static readonly Brush HairBrush = Freeze(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)));
        public static readonly Brush RowBgBrush = Freeze(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)));
        public static readonly Brush RowHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)));

        public static readonly Brush CardBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)));
        public static readonly Brush StageBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)));

        public static Brush GetToneBrush(WidgetTone tone)
        {
            switch (tone)
            {
                case WidgetTone.Ok: return OkBrush;
                case WidgetTone.Warn: return WarnBrush;
                case WidgetTone.Danger: return DangerBrush;
                case WidgetTone.Off: return OffBrush;
                case WidgetTone.Neutral:
                default: return Text2Brush;
            }
        }

        public static Brush GetBarFillBrush(WidgetTone tone)
        {
            switch (tone)
            {
                case WidgetTone.Ok: return OkBrush;
                case WidgetTone.Warn: return WarnBrush;
                case WidgetTone.Danger: return DangerBrush;
                case WidgetTone.Off: return OffBrush;
                case WidgetTone.Neutral:
                default: return DefaultFillBrush;
            }
        }

        public static LinearGradientBrush CreateWidgetBackgroundBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 28, 33, 44), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(190, 18, 21, 28), 0.45));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(215, 24, 28, 38), 1.0));
            brush.Freeze();
            return brush;
        }

        public static RadialGradientBrush CreateStageBackgroundBrush()
        {
            var brush = new RadialGradientBrush
            {
                Center = new System.Windows.Point(0.25, 0.2),
                RadiusX = 0.8,
                RadiusY = 0.8,
                GradientOrigin = new System.Windows.Point(0.25, 0.2)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(35, 107, 166, 255), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(15, 180, 120, 255), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(8, 255, 255, 255), 1.0));
            brush.Freeze();
            return brush;
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
