using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AutoBrightnessApp.Services;

namespace AutoBrightnessApp.Controls;

/// <summary>
/// DrawingContext へ直接描画するゼロ UIElement 構成の軽量履歴チャート
/// </summary>
public sealed class MeasurementHistoryChart : FrameworkElement
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(5);

    private static readonly SolidColorBrush BgBrush = CreateFrozenBrush(Color.FromRgb(248, 250, 252));
    private static readonly SolidColorBrush AxisTextBrush = CreateFrozenBrush(Color.FromRgb(100, 116, 139));
    private static readonly SolidColorBrush ScoreBrush = CreateFrozenBrush(Color.FromRgb(249, 115, 22));
    private static readonly SolidColorBrush BrightnessBrush = CreateFrozenBrush(Color.FromRgb(37, 99, 235));

    private static readonly Pen BorderPen = CreateFrozenPen(Color.FromRgb(226, 232, 240), 1.0);
    private static readonly Pen GridSolidPen = CreateFrozenPen(Color.FromRgb(226, 232, 240), 1.0);
    private static readonly Pen GridDashedPen = CreateFrozenPen(Color.FromRgb(226, 232, 240), 1.0, [3, 3]);
    private static readonly Pen GridVerticalDashedPen = CreateFrozenPen(Color.FromRgb(226, 232, 240), 1.0, [2, 4]);
    private static readonly Pen ScoreLinePen = CreateFrozenPen(Color.FromRgb(249, 115, 22), 2.0);
    private static readonly Pen BrightnessLinePen = CreateFrozenPen(Color.FromRgb(37, 99, 235), 2.0);

    private static readonly Typeface UiTypeface = new("Segoe UI, Meiryo UI");

    private IReadOnlyList<HistoryEntry> _entries = [];

    public MeasurementHistoryChart()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public void UpdateHistory(IReadOnlyList<HistoryEntry>? history)
    {
        _entries = history ?? [];
        InvalidateVisual();
    }

    public void Redraw()
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 80 || height < 60) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 外枠と背景
        dc.DrawRoundedRectangle(BgBrush, BorderPen, new Rect(0.5, 0.5, width - 1, height - 1), 4, 4);

        // 上部凡例
        DrawLegend(dc, width, dpi);

        const double leftMargin = 46;
        const double rightMargin = 42;
        const double topMargin = 26;
        const double bottomMargin = 22;

        double plotW = width - leftMargin - rightMargin;
        double plotH = height - topMargin - bottomMargin;
        if (plotW <= 10 || plotH <= 10) return;

        if (_entries.Count == 0)
        {
            var emptyFt = CreateText(LocalizationService.Get("HistoryEmpty"), 12, AxisTextBrush, dpi);
            dc.DrawText(emptyFt, new Point((width - emptyFt.Width) / 2.0, topMargin + (plotH - emptyFt.Height) / 2.0));
            return;
        }

        double maxObservedScore = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].NormalizedScore > maxObservedScore)
            {
                maxObservedScore = _entries[i].NormalizedScore;
            }
        }
        double maxScoreAxis = Math.Max(300.0, Math.Ceiling((maxObservedScore * 1.15) / 100.0) * 100.0);

        // 水平グリッドと左右Y軸ラベル
        for (int i = 0; i <= 4; i++)
        {
            double ratio = i / 4.0;
            double y = topMargin + plotH * (1.0 - ratio);

            dc.DrawLine(i is 0 or 4 ? GridSolidPen : GridDashedPen,
                new Point(leftMargin, y),
                new Point(leftMargin + plotW, y));

            var leftFt = CreateText((maxScoreAxis * ratio).ToString("F0", CultureInfo.InvariantCulture), 10, ScoreBrush, dpi);
            dc.DrawText(leftFt, new Point(Math.Max(2, leftMargin - leftFt.Width - 5), y - leftFt.Height / 2.0));

            int brightTick = (int)Math.Round(100.0 * ratio);
            var rightFt = CreateText($"{brightTick}%", 10, BrightnessBrush, dpi);
            dc.DrawText(rightFt, new Point(leftMargin + plotW + 5, y - rightFt.Height / 2.0));
        }

        // 垂直グリッドと時刻ラベル
        DateTime endTime = _entries[^1].Timestamp;
        DateTime startTime = endTime - WindowDuration;

        for (int m = 0; m <= 5; m++)
        {
            double ratio = m / 5.0;
            double x = leftMargin + plotW * ratio;

            dc.DrawLine(m is 0 or 5 ? GridSolidPen : GridVerticalDashedPen,
                new Point(x, topMargin),
                new Point(x, topMargin + plotH));

            DateTime tickTime = startTime + TimeSpan.FromMinutes(m);
            var timeFt = CreateText(tickTime.ToString("HH:mm", CultureInfo.InvariantCulture), 10, AxisTextBrush, dpi);
            double labelX = Math.Clamp(x - timeFt.Width / 2.0, 2, width - timeFt.Width - 2);
            dc.DrawText(timeFt, new Point(labelX, topMargin + plotH + 4));
        }

        // StreamGeometry で折れ線を一括描画
        double totalSeconds = WindowDuration.TotalSeconds;
        var scoreGeo = new StreamGeometry();
        var brightGeo = new StreamGeometry();
        Point lastScorePt = default;
        Point lastBrightPt = default;

        using (var sCtx = scoreGeo.Open())
        using (var bCtx = brightGeo.Open())
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                double elapsedSec = Math.Clamp((entry.Timestamp - startTime).TotalSeconds, 0.0, totalSeconds);
                double x = leftMargin + (elapsedSec / totalSeconds) * plotW;

                double scoreNorm = Math.Clamp(entry.NormalizedScore / maxScoreAxis, 0.0, 1.0);
                double yScore = topMargin + plotH * (1.0 - scoreNorm);
                var sPt = new Point(x, yScore);

                double brightNorm = Math.Clamp(entry.AppliedBrightness / 100.0, 0.0, 1.0);
                double yBright = topMargin + plotH * (1.0 - brightNorm);
                var bPt = new Point(x, yBright);

                if (i == 0)
                {
                    sCtx.BeginFigure(sPt, false, false);
                    bCtx.BeginFigure(bPt, false, false);
                }
                else
                {
                    sCtx.LineTo(sPt, true, true);
                    bCtx.LineTo(bPt, true, true);
                }

                lastScorePt = sPt;
                lastBrightPt = bPt;
            }
        }

        scoreGeo.Freeze();
        brightGeo.Freeze();

        if (_entries.Count >= 2)
        {
            dc.DrawGeometry(null, BrightnessLinePen, brightGeo);
            dc.DrawGeometry(null, ScoreLinePen, scoreGeo);
        }

        dc.DrawEllipse(BrightnessBrush, null, lastBrightPt, 3.0, 3.0);
        dc.DrawEllipse(ScoreBrush, null, lastScorePt, 3.0, 3.0);
    }

    private static void DrawLegend(DrawingContext dc, double width, double dpi)
    {
        var scoreFt = CreateText(LocalizationService.Get("LegendScore"), 11, AxisTextBrush, dpi);
        var brightFt = CreateText(LocalizationService.Get("LegendBrightness"), 11, AxisTextBrush, dpi);

        const double swatchW = 12;
        const double swatchGap = 4;
        const double itemGap = 14;

        double totalW = swatchW + swatchGap + scoreFt.Width + itemGap + swatchW + swatchGap + brightFt.Width;
        double x = Math.Max(8, (width - totalW) / 2.0);
        const double yTop = 5.0;

        dc.DrawRoundedRectangle(ScoreBrush, null, new Rect(x, yTop + scoreFt.Height / 2.0 - 1.5, swatchW, 3), 1.5, 1.5);
        x += swatchW + swatchGap;
        dc.DrawText(scoreFt, new Point(x, yTop));
        x += scoreFt.Width + itemGap;

        dc.DrawRoundedRectangle(BrightnessBrush, null, new Rect(x, yTop + brightFt.Height / 2.0 - 1.5, swatchW, 3), 1.5, 1.5);
        x += swatchW + swatchGap;
        dc.DrawText(brightFt, new Point(x, yTop));
    }

    private static FormattedText CreateText(string text, double fontSize, Brush brush, double dpi)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            UiTypeface,
            fontSize,
            brush,
            dpi);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness, double[]? dashes = null)
    {
        var brush = CreateFrozenBrush(color);
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        if (dashes != null)
        {
            pen.DashStyle = new DashStyle(dashes, 0);
        }
        pen.Freeze();
        return pen;
    }
}