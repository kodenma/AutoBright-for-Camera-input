using AutoBrightnessApp.Models;

namespace AutoBrightnessApp.Services;

/// <summary>
/// ウェーバー・フェヒナー対数知覚空間における Fritsch-Carlson 単調3次エルミート補間（PCHIP）
/// </summary>
public static class ErgonomicCurveInterpolator
{
    public const double MinScore = 0.0;
    public const double MaxScore = 700.0;
    private const double DarkAdaptationBias = 25.0; // 暗順応バイアス定数

    /// <summary>
    /// 物理照度スコアを人間の明度知覚に近い対数空間座標へ変換
    /// </summary>
    public static double ToPerceptualX(double score)
        => Math.Log(Math.Max(0.0, score) + DarkAdaptationBias);

    /// <summary>
    /// 対数空間座標から物理照度スコアへ逆変換
    /// </summary>
    public static double FromPerceptualX(double perceptualX)
        => Math.Max(0.0, Math.Exp(perceptualX) - DarkAdaptationBias);

    /// <summary>
    /// 制御点リストから滑らかで単調な目標画面輝度（0.0〜100.0）を算出
    /// </summary>
    public static double Evaluate(IReadOnlyList<BrightnessPoint> rawPoints, double score)
    {
        if (rawPoints.Count == 0) return 50.0;

        var pts = rawPoints
            .OrderBy(p => p.InputScore)
            .Select(p => (X: ToPerceptualX(p.InputScore), Y: (double)Math.Clamp(p.TargetBrightness, 0, 100)))
            .ToList();

        // 重複X座標の除去
        for (int i = pts.Count - 1; i > 0; i--)
        {
            if (Math.Abs(pts[i].X - pts[i - 1].X) < 1e-6)
                pts.RemoveAt(i);
        }

        int n = pts.Count;
        if (n == 1) return pts[0].Y;

        double targetX = ToPerceptualX(score);
        if (targetX <= pts[0].X) return pts[0].Y;
        if (targetX >= pts[^1].X) return pts[^1].Y;

        // 区間幅 h と割線傾き delta を計算
        double[] h = new double[n - 1];
        double[] delta = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = pts[i + 1].X - pts[i].X;
            delta[i] = (pts[i + 1].Y - pts[i].Y) / h[i];
        }

        // Fritsch-Carlson 法による各制御点の接線傾き m の算出（単調性の保証）
        double[] m = new double[n];
        m[0] = delta[0];
        m[n - 1] = delta[n - 2];

        for (int i = 1; i < n - 1; i++)
        {
            // 符号が反転または平坦な点では傾きを0にし、オーバーシュートを完全に防止する
            if (delta[i - 1] * delta[i] <= 0.0)
            {
                m[i] = 0.0;
            }
            else
            {
                double w1 = 2.0 * h[i] + h[i - 1];
                double w2 = h[i] + 2.0 * h[i - 1];
                m[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
            }
        }

        // 端点の傾き制限（Fritsch-Carlson 条件）
        if (delta[0] == 0.0) m[0] = 0.0;
        else if (m[0] / delta[0] > 3.0) m[0] = 3.0 * delta[0];
        else if (m[0] / delta[0] < 0.0) m[0] = 0.0;

        if (delta[n - 2] == 0.0) m[n - 1] = 0.0;
        else if (m[n - 1] / delta[n - 2] > 3.0) m[n - 1] = 3.0 * delta[n - 2];
        else if (m[n - 1] / delta[n - 2] < 0.0) m[n - 1] = 0.0;

        // 対象区間の3次エルミート多項式を評価
        for (int i = 0; i < n - 1; i++)
        {
            if (targetX >= pts[i].X && targetX <= pts[i + 1].X)
            {
                double t = (targetX - pts[i].X) / h[i];
                double t2 = t * t;
                double t3 = t2 * t;

                double h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
                double h10 = t3 - 2.0 * t2 + t;
                double h01 = -2.0 * t3 + 3.0 * t2;
                double h11 = t3 - t2;

                double val = h00 * pts[i].Y + h10 * h[i] * m[i] + h01 * pts[i + 1].Y + h11 * h[i] * m[i + 1];
                return Math.Clamp(val, 0.0, 100.0);
            }
        }

        return pts[^1].Y;
    }
}