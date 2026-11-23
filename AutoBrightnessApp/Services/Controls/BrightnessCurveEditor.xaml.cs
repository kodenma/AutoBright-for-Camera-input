using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AutoBrightnessApp.Models;
using AutoBrightnessApp.Services;

namespace AutoBrightnessApp.Controls;

public partial class BrightnessCurveEditor : UserControl
{
    private const double PadLeft = 44.0;
    private const double PadRight = 18.0;
    private const double PadTop = 14.0;
    private const double PadBottom = 30.0;

    private List<BrightnessPoint> _points = [];
    private double? _currentMeasuredScore;
    private int _draggingIndex = -1;

    public event Action? CurveChanged;

    public BrightnessCurveEditor()
    {
        InitializeComponent();
    }

    public IReadOnlyList<BrightnessPoint> Points => _points;

    public void SetPoints(IEnumerable<BrightnessPoint> points)
    {
        _points = points.OrderBy(p => p.InputScore).ToList();
        Redraw();
    }

    public void SetCurrentMeasuredScore(double? score)
    {
        _currentMeasuredScore = score;
        Redraw();
    }

    private double PlotWidth => Math.Max(10.0, PlotCanvas.ActualWidth - PadLeft - PadRight);
    private double PlotHeight => Math.Max(10.0, PlotCanvas.ActualHeight - PadTop - PadBottom);

    private double ScoreToCanvasX(double score)
    {
        double xMin = ErgonomicCurveInterpolator.ToPerceptualX(ErgonomicCurveInterpolator.MinScore);
        double xMax = ErgonomicCurveInterpolator.ToPerceptualX(ErgonomicCurveInterpolator.MaxScore);
        double xVal = ErgonomicCurveInterpolator.ToPerceptualX(Math.Clamp(score, ErgonomicCurveInterpolator.MinScore, ErgonomicCurveInterpolator.MaxScore));
        double ratio = (xVal - xMin) / (xMax - xMin);
        return PadLeft + ratio * PlotWidth;
    }

    private double CanvasXToScore(double canvasX)
    {
        double ratio = Math.Clamp((canvasX - PadLeft) / PlotWidth, 0.0, 1.0);
        double xMin = ErgonomicCurveInterpolator.ToPerceptualX(ErgonomicCurveInterpolator.MinScore);
        double xMax = ErgonomicCurveInterpolator.ToPerceptualX(ErgonomicCurveInterpolator.MaxScore);
        return ErgonomicCurveInterpolator.FromPerceptualX(xMin + ratio * (xMax - xMin));
    }

    private double BrightnessToCanvasY(double brightness)
    {
        double ratio = Math.Clamp(brightness, 0.0, 100.0) / 100.0;
        return PadTop + (1.0 - ratio) * PlotHeight;
    }

    private int CanvasYToBrightness(double canvasY)
    {
        double ratio = 1.0 - Math.Clamp((canvasY - PadTop) / PlotHeight, 0.0, 1.0);
        return (int)Math.Round(ratio * 100.0);
    }

    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    public void Redraw()
    {
        if (PlotCanvas.ActualWidth <= 0 || PlotCanvas.ActualHeight <= 0) return;
        PlotCanvas.Children.Clear();

        DrawGridAndAxes();
        DrawSmoothCurve();
        DrawCurrentScoreIndicator();
        DrawControlHandles();
    }

    private void DrawGridAndAxes()
    {
        var gridBrush = new SolidColorBrush(Color.FromRgb(235, 240, 246));
        var axisBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        var labelBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));

        for (int b = 0; b <= 100; b += 25)
        {
            double y = BrightnessToCanvasY(b);
            PlotCanvas.Children.Add(new Line
            {
                X1 = PadLeft,
                Y1 = y,
                X2 = PadLeft + PlotWidth,
                Y2 = y,
                Stroke = b == 0 ? axisBrush : gridBrush,
                StrokeThickness = 1
            });

            var lbl = new TextBlock { Text = $"{b}%", Foreground = labelBrush, FontSize = 10 };
            Canvas.SetLeft(lbl, 6);
            Canvas.SetTop(lbl, y - 7);
            PlotCanvas.Children.Add(lbl);
        }

        double[] xTicks = [0, 30, 80, 170, 350, 700];
        foreach (double score in xTicks)
        {
            double x = ScoreToCanvasX(score);
            PlotCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = PadTop,
                X2 = x,
                Y2 = PadTop + PlotHeight,
                Stroke = score == 0 ? axisBrush : gridBrush,
                StrokeThickness = 1
            });

            var lbl = new TextBlock { Text = $"{score:F0}", Foreground = labelBrush, FontSize = 10 };
            Canvas.SetLeft(lbl, x - (score >= 100 ? 10 : 6));
            Canvas.SetTop(lbl, PadTop + PlotHeight + 4);
            PlotCanvas.Children.Add(lbl);
        }

        var xTitle = new TextBlock { Text = LocalizationService.Get("AxisScore"), Foreground = labelBrush, FontSize = 10 };
        Canvas.SetLeft(xTitle, PadLeft + PlotWidth / 2 - 45);
        Canvas.SetTop(xTitle, PadTop + PlotHeight + 16);
        PlotCanvas.Children.Add(xTitle);
    }

    private void DrawSmoothCurve()
    {
        if (_points.Count == 0) return;

        var polyPoints = new PointCollection();
        var areaPoints = new PointCollection { new Point(PadLeft, PadTop + PlotHeight) };

        int steps = Math.Max(120, (int)PlotWidth);
        for (int i = 0; i <= steps; i++)
        {
            double px = PadLeft + (PlotWidth * i / steps);
            double score = CanvasXToScore(px);
            double brightness = ErgonomicCurveInterpolator.Evaluate(_points, score);
            double py = BrightnessToCanvasY(brightness);

            polyPoints.Add(new Point(px, py));
            areaPoints.Add(new Point(px, py));
        }
        areaPoints.Add(new Point(PadLeft + PlotWidth, PadTop + PlotHeight));

        PlotCanvas.Children.Add(new Polygon
        {
            Points = areaPoints,
            Fill = new SolidColorBrush(Color.FromArgb(28, 37, 99, 235))
        });

        PlotCanvas.Children.Add(new Polyline
        {
            Points = polyPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void DrawCurrentScoreIndicator()
    {
        if (_currentMeasuredScore is not double score || _points.Count == 0) return;

        double x = ScoreToCanvasX(score);
        double brightness = ErgonomicCurveInterpolator.Evaluate(_points, score);
        double y = BrightnessToCanvasY(brightness);

        PlotCanvas.Children.Add(new Line
        {
            X1 = x,
            Y1 = PadTop,
            X2 = x,
            Y2 = PadTop + PlotHeight,
            Stroke = new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3]
        });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(dot, x - 4);
        Canvas.SetTop(dot, y - 4);
        PlotCanvas.Children.Add(dot);
    }

    private void DrawControlHandles()
    {
        for (int i = 0; i < _points.Count; i++)
        {
            int index = i;
            var pt = _points[i];
            double cx = ScoreToCanvasX(pt.InputScore);
            double cy = BrightnessToCanvasY(pt.TargetBrightness);
            bool isActive = _draggingIndex == index;

            double radius = isActive ? 7.0 : 5.5;
            var handle = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = isActive ? new SolidColorBrush(Color.FromRgb(29, 78, 216)) : Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                StrokeThickness = 2.5,
                Cursor = Cursors.Hand,
                ToolTip = $"{LocalizationService.Get("AxisScore")}: {pt.InputScore:F0} → {LocalizationService.Get("AxisBrightness")}: {pt.TargetBrightness}%"
            };

            handle.MouseLeftButtonDown += (s, e) =>
            {
                _draggingIndex = index;
                PlotCanvas.CaptureMouse();
                ShowCoordBadge(pt.InputScore, pt.TargetBrightness);
                Redraw();
                e.Handled = true;
            };

            handle.MouseRightButtonDown += (s, e) =>
            {
                if (_points.Count > 2)
                {
                    _points.RemoveAt(index);
                    CurveChanged?.Invoke();
                    Redraw();
                }
                e.Handled = true;
            };

            Canvas.SetLeft(handle, cx - radius);
            Canvas.SetTop(handle, cy - radius);
            PlotCanvas.Children.Add(handle);
        }
    }

    private void PlotCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            var pos = e.GetPosition(PlotCanvas);
            double newScore = Math.Round(CanvasXToScore(pos.X));
            int newBrightness = CanvasYToBrightness(pos.Y);

            if (_points.All(p => Math.Abs(p.InputScore - newScore) >= 4.0))
            {
                _points.Add(new BrightnessPoint(newScore, newBrightness));
                _points = _points.OrderBy(p => p.InputScore).ToList();
                CurveChanged?.Invoke();
                Redraw();
            }
        }
    }

    private void PlotCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingIndex < 0 || _draggingIndex >= _points.Count) return;

        var pos = e.GetPosition(PlotCanvas);
        double rawScore = Math.Round(CanvasXToScore(pos.X));
        int newBrightness = CanvasYToBrightness(pos.Y);

        double minAllowed = _draggingIndex > 0 ? _points[_draggingIndex - 1].InputScore + 3.0 : ErgonomicCurveInterpolator.MinScore;
        double maxAllowed = _draggingIndex < _points.Count - 1 ? _points[_draggingIndex + 1].InputScore - 3.0 : ErgonomicCurveInterpolator.MaxScore;
        double clampedScore = Math.Clamp(rawScore, minAllowed, maxAllowed);

        _points[_draggingIndex] = new BrightnessPoint(clampedScore, newBrightness);
        ShowCoordBadge(clampedScore, newBrightness);
        CurveChanged?.Invoke();
        Redraw();
    }

    private void PlotCanvas_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_draggingIndex >= 0)
        {
            _draggingIndex = -1;
            PlotCanvas.ReleaseMouseCapture();
            CoordBadge.Visibility = Visibility.Collapsed;
            Redraw();
        }
    }

    private void ShowCoordBadge(double score, int brightness)
    {
        CoordText.Text = $"{LocalizationService.Get("AxisScore")}: {score:F0}  |  {brightness}%";
        CoordBadge.Visibility = Visibility.Visible;
    }
}