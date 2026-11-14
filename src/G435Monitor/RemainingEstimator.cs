namespace G435Monitor;

/// <summary>
/// Estima o tempo restante por regressão linear da porcentagem bruta
/// durante a descarga. O histórico já vem convertido pela curva de tensão
/// do <see cref="BatteryService"/>.
/// </summary>
internal static class RemainingEstimator
{
    public const int MinPoints = 3;
    public const double MinSpanMinutes = 15;

    // Inclinação menor que isso (em módulo) é plana demais para estimar.
    public const double MinSlopePctPerMinute = 0.005;

    public const double MaxMinutes = 48 * 60;

    public static double? EstimateMinutes(
        IReadOnlyList<(DateTime Time, int Pct)> points, int currentRawPct)
    {
        if (points.Count < MinPoints)
            return null;

        DateTime origin = points[0].Time;
        double spanMinutes = (points[^1].Time - origin).TotalMinutes;
        if (spanMinutes < MinSpanMinutes)
            return null;

        double meanX = 0;
        double meanY = 0;
        foreach ((DateTime time, int pct) in points)
        {
            meanX += (time - origin).TotalMinutes;
            meanY += pct;
        }

        meanX /= points.Count;
        meanY /= points.Count;

        double numerator = 0;
        double denominator = 0;
        foreach ((DateTime time, int pct) in points)
        {
            double x = (time - origin).TotalMinutes - meanX;
            double y = pct - meanY;
            numerator += x * y;
            denominator += x * x;
        }

        if (denominator <= 0)
            return null;

        double slope = numerator / denominator; // % por minuto

        // Só vale queda real, e não uma variação plana/ruidosa.
        if (slope > -MinSlopePctPerMinute)
            return null;

        return currentRawPct / -slope;
    }

    public static string? Format(double? minutes)
    {
        if (minutes is not double m)
            return null;

        if (m > MaxMinutes)
            return "> 48 h";

        if (m < 10)
            return "< 10 min";

        if (m < 90)
        {
            // Passos de 5 min; se arredondar para 90, mantém abaixo do limite.
            int rounded = (int)(Math.Round(m / 5, MidpointRounding.AwayFromZero) * 5);
            return $"~{Math.Min(rounded, 85)} min";
        }

        int hours = (int)Math.Round(m / 60, MidpointRounding.AwayFromZero);
        return $"~{hours} h";
    }
}
