using System;
using System.Windows;
using System.Windows.Media;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 提供纯矢量的设备链路与状态图标，自适应 DPI 且边缘清晰。
    /// </summary>
    public static class DeviceIcons
    {
        private static readonly Brush DefaultIconBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));

        static DeviceIcons()
        {
            DefaultIconBrush.Freeze();
        }

        /// <summary>蓝牙符文图标 (15x15 DIP)</summary>
        public static DrawingImage CreateBluetoothIcon(Brush brush = null)
        {
            var b = brush ?? DefaultIconBrush;
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var pen = new Pen(b, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                pen.Freeze();

                // 蓝牙符文线条: (7.5, 1.5) -> (7.5, 13.5)
                // (3.5, 4.5) -> (11.5, 10.5) -> (7.5, 13.5) -> (7.5, 1.5) -> (11.5, 4.5) -> (3.5, 10.5)
                var stream = new StreamGeometry();
                using (var sc = stream.Open())
                {
                    sc.BeginFigure(new Point(7.5, 1.5), false, false);
                    sc.LineTo(new Point(7.5, 13.5), true, false);

                    sc.BeginFigure(new Point(3.5, 4.5), false, false);
                    sc.LineTo(new Point(11.5, 10.5), true, false);
                    sc.LineTo(new Point(7.5, 13.5), true, false);
                    sc.LineTo(new Point(7.5, 1.5), true, false);
                    sc.LineTo(new Point(11.5, 4.5), true, false);
                    sc.LineTo(new Point(3.5, 10.5), true, false);
                }
                stream.Freeze();
                ctx.DrawGeometry(null, pen, stream);
            }
            group.Freeze();
            return new DrawingImage(group);
        }

        /// <summary>2.4G 外设图标：实心点 + 两道同心弧 (15x15 DIP)</summary>
        public static DrawingImage Create24GIcon(Brush brush = null)
        {
            var b = brush ?? DefaultIconBrush;
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var pen = new Pen(b, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();

                // 中心发射点
                ctx.DrawEllipse(b, null, new Point(7.5, 11.5), 1.4, 1.4);

                // 内弧
                var arc1 = new PathGeometry();
                var fig1 = new PathFigure { StartPoint = new Point(4.5, 8.5), IsClosed = false };
                fig1.Segments.Add(new ArcSegment(new Point(10.5, 8.5), new Size(4, 4), 0, false, SweepDirection.Clockwise, true));
                arc1.Figures.Add(fig1);
                arc1.Freeze();
                ctx.DrawGeometry(null, pen, arc1);

                // 外弧
                var arc2 = new PathGeometry();
                var fig2 = new PathFigure { StartPoint = new Point(2.0, 5.5), IsClosed = false };
                fig2.Segments.Add(new ArcSegment(new Point(13.0, 5.5), new Size(7.5, 7.5), 0, false, SweepDirection.Clockwise, true));
                arc2.Figures.Add(fig2);
                arc2.Freeze();
                ctx.DrawGeometry(null, pen, arc2);
            }
            group.Freeze();
            return new DrawingImage(group);
        }

        /// <summary>USB 有线图标：主干 + 箭头 + 分支 (15x15 DIP)</summary>
        public static DrawingImage CreateUsbIcon(Brush brush = null)
        {
            var b = brush ?? DefaultIconBrush;
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var pen = new Pen(b, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();

                // 底部圆环
                ctx.DrawEllipse(null, pen, new Point(7.5, 12.5), 1.2, 1.2);

                // 主干垂直线 (7.5, 11.3) -> (7.5, 2.5)
                ctx.DrawLine(pen, new Point(7.5, 11.3), new Point(7.5, 3.0));

                // 顶部箭头
                var arrow = new StreamGeometry();
                using (var ac = arrow.Open())
                {
                    ac.BeginFigure(new Point(5.5, 4.0), true, true);
                    ac.LineTo(new Point(7.5, 1.2), true, false);
                    ac.LineTo(new Point(9.5, 4.0), true, false);
                }
                arrow.Freeze();
                ctx.DrawGeometry(b, null, arrow);

                // 左侧分支带方块
                var leftBranch = new StreamGeometry();
                using (var lc = leftBranch.Open())
                {
                    lc.BeginFigure(new Point(7.5, 8.5), false, false);
                    lc.LineTo(new Point(4.2, 6.5), true, false);
                    lc.LineTo(new Point(4.2, 4.8), true, false);
                }
                leftBranch.Freeze();
                ctx.DrawGeometry(null, pen, leftBranch);
                ctx.DrawRectangle(b, null, new Rect(3.2, 3.8, 2.0, 2.0));

                // 右侧分支带圆点
                var rightBranch = new StreamGeometry();
                using (var rc = rightBranch.Open())
                {
                    rc.BeginFigure(new Point(7.5, 6.5), false, false);
                    rc.LineTo(new Point(10.8, 8.5), true, false);
                    rc.LineTo(new Point(10.8, 10.0), true, false);
                }
                rightBranch.Freeze();
                ctx.DrawGeometry(null, pen, rightBranch);
                ctx.DrawEllipse(b, null, new Point(10.8, 10.5), 1.2, 1.2);
            }
            group.Freeze();
            return new DrawingImage(group);
        }

        /// <summary>本机电池图标：笔记本轮廓 (15x15 DIP)</summary>
        public static DrawingImage CreateLaptopIcon(Brush brush = null)
        {
            var b = brush ?? DefaultIconBrush;
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var pen = new Pen(b, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                pen.Freeze();

                // 笔记本屏幕
                ctx.DrawRoundedRectangle(null, pen, new Rect(3.0, 2.5, 9.0, 6.5), 1.0, 1.0);

                // 底座键盘区
                var baseGeo = new StreamGeometry();
                using (var bc = baseGeo.Open())
                {
                    bc.BeginFigure(new Point(1.5, 11.5), true, true);
                    bc.LineTo(new Point(13.5, 11.5), true, false);
                    bc.LineTo(new Point(12.5, 9.5), true, false);
                    bc.LineTo(new Point(2.5, 9.5), true, false);
                }
                baseGeo.Freeze();
                ctx.DrawGeometry(null, pen, baseGeo);
            }
            group.Freeze();
            return new DrawingImage(group);
        }

        /// <summary>未知/待确认外设图标 (15x15 DIP)</summary>
        public static DrawingImage CreateUnknownIcon(Brush brush = null)
        {
            var b = brush ?? DefaultIconBrush;
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var pen = new Pen(b, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();

                // 圆角方形
                ctx.DrawRoundedRectangle(null, pen, new Rect(2.5, 2.5, 10.0, 10.0), 2.5, 2.5);
                // 中心问号点或圆点
                ctx.DrawEllipse(b, null, new Point(7.5, 7.5), 1.6, 1.6);
            }
            group.Freeze();
            return new DrawingImage(group);
        }

        /// <summary>闪电充电图标 (12x12 DIP)</summary>
        public static DrawingImage CreateChargingIcon(Brush brush = null)
        {
            var b = brush ?? new SolidColorBrush(Color.FromRgb(52, 211, 153)); // --ok #34D399
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var bolt = new StreamGeometry();
                using (var bc = bolt.Open())
                {
                    bc.BeginFigure(new Point(6.8, 1.0), true, true);
                    bc.LineTo(new Point(2.5, 6.8), true, false);
                    bc.LineTo(new Point(5.8, 6.8), true, false);
                    bc.LineTo(new Point(5.0, 11.5), true, false);
                    bc.LineTo(new Point(9.5, 5.2), true, false);
                    bc.LineTo(new Point(6.2, 5.2), true, false);
                }
                bolt.Freeze();
                ctx.DrawGeometry(b, null, bolt);
            }
            group.Freeze();
            return new DrawingImage(group);
        }

        /// <summary>无设备空态大图标 (48x48 DIP)</summary>
        public static DrawingImage CreateEmptyStateIcon()
        {
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 2.0)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();

                // 电池壳 (14, 10) 到 (34, 38)
                ctx.DrawRoundedRectangle(null, pen, new Rect(14, 12, 20, 28), 4, 4);
                // 电池正极极柱
                ctx.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, new Rect(20, 7, 8, 4), 1.5, 1.5);

                // 搜索/虚线感应点
                var dotPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 107, 166, 255)), 2.0)
                {
                    DashStyle = DashStyles.Dot
                };
                dotPen.Freeze();
                ctx.DrawEllipse(null, dotPen, new Point(24, 26), 7, 7);
            }
            group.Freeze();
            return new DrawingImage(group);
        }
    }
}
