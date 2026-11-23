using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoBrightnessApp.Services;

public static class AppIconGenerator
{
    private static ImageSource? _cachedIcon;

    public static ImageSource GetAppIcon()
    {
        if (_cachedIcon != null) return _cachedIcon;

        const int size = 128;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            // 1. 背景：深いロイヤルブルーの角丸プレート
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(30, 58, 138),
                Color.FromRgb(37, 99, 235),
                new Point(0, 0),
                new Point(1, 1));
            dc.DrawRoundedRectangle(bgBrush, null, new Rect(6, 6, size - 12, size - 12), 28, 28);

            // 2. 太陽の放射光線（アンバーゴールド）
            var rayPen = new Pen(new SolidColorBrush(Color.FromRgb(251, 191, 36)), 6.0)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            var center = new Point(size / 2.0, size / 2.0);
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4.0;
                double r1 = 36.0;
                double r2 = 46.0;
                var p1 = new Point(center.X + r1 * Math.Cos(angle), center.Y + r1 * Math.Sin(angle));
                var p2 = new Point(center.X + r2 * Math.Cos(angle), center.Y + r2 * Math.Sin(angle));
                dc.DrawLine(rayPen, p1, p2);
            }

            // 3. カメラレンズ外周リング（ホワイト）
            var ringPen = new Pen(Brushes.White, 6.0);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(15, 23, 42)), ringPen, center, 27, 27);

            // 4. レンズ内部の光学ガラス（シアンブルー）
            var lensBrush = new RadialGradientBrush(
                Color.FromRgb(56, 189, 248),
                Color.FromRgb(3, 105, 161));
            dc.DrawEllipse(lensBrush, null, center, 17, 17);

            // 5. レンズのキャッチライト反射
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, new Point(center.X - 6, center.Y - 6), 5, 5);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        _cachedIcon = rtb;
        return rtb;
    }
}