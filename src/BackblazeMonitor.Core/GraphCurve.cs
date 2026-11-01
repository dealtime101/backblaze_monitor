namespace BackblazeMonitor.Core;

/// <summary>A point of the chart, in chart pixels (y grows downward).</summary>
public readonly record struct GraphPoint(float X, float Y);

/// <summary>Result of a chart hover: marker column and tooltip text.</summary>
/// <param name="X">Column of the marker line.</param>
/// <param name="Text">Tooltip text.</param>
public sealed record GraphHit(double X, string Text);

/// <summary>Chart geometry: returns data, never draws.</summary>
public static class GraphCurve
{
    /// <summary>Chart y-axis scale: floor at 1 Mbps, then 15 % headroom.</summary>
    public static double ScaleMax(double maxBits)
    {
        if (maxBits < 1_000_000) maxBits = 1_000_000;
        return maxBits * 1.15;
    }

    /// <summary>
    /// 30-minute curve for a chart <paramref name="w"/> wide and <paramref name="h"/> high: one point per
    /// block, at zero in the gaps, and at the right edge held level while sending continues, otherwise
    /// dropped back to zero. <paramref name="pts"/> must be sorted by time; empty yields an empty list.
    /// </summary>
    public static List<GraphPoint> Get(IReadOnlyList<LogBlock> pts, DateTime now, int w, int h)
    {
        var seq = new List<GraphPoint>();
        if (pts.Count == 0) return seq;
        var window = (double)BzConstants.WindowSec;
        var baseY = h - 2;
        var t0 = now.AddSeconds(-BzConstants.WindowSec);

        // Total rate (sum of the threads), like the current rate, not the rate of one block
        var series = RateCalculator.GetRateSeries(pts);
        var max = series.Max();
        max = ScaleMax(max);

        var prevT = t0;
        var k = -1;
        foreach (var p in pts)
        {
            k++;
            var x = (float)(((p.Time - t0).TotalSeconds / window) * w);
            var y = (float)(baseY - ((series[k] / max) * (h - 5)));
            // Gap in transmissions: drop back to zero so as not to suggest continuous sending
            if (RateCalculator.IsHole(prevT, p))
            {
                var xa = (float)(((prevT - t0).TotalSeconds / window) * w);
                seq.Add(new GraphPoint(xa, baseY));
                seq.Add(new GraphPoint(x, baseY));
            }

            seq.Add(new GraphPoint(x, y));
            prevT = p.Time;
        }

        // Right edge: extend if it is still transmitting, otherwise drop
        var yEnd = (float)baseY;
        if (RateCalculator.IsSending(pts[^1], now)) yEnd = seq[^1].Y;
        seq.Add(new GraphPoint(w, yEnd));
        return seq;
    }

    /// <summary>
    /// Chart for a history period (24 h, 7 d): one flat step per slot. Empty when the history holds no
    /// byte.
    /// </summary>
    public static List<GraphPoint> GetHistory(HistoryResult? hist, int w, int h)
    {
        var seq = new List<GraphPoint>();
        if (hist is null || hist.Bytes <= 0) return seq;
        var baseY = h - 2;
        var r = hist.Rates;
        var max = ScaleMax(r.Max());
        for (var i = 0; i < r.Length; i++)
        {
            var y = (float)(baseY - ((r[i] / max) * (h - 5)));
            seq.Add(new GraphPoint((float)((double)i * w / r.Length), y));
            seq.Add(new GraphPoint((float)((double)(i + 1) * w / r.Length), y));
        }

        return seq;
    }

    /// <summary>
    /// Chart hover: the block (30 min) or slot (24 h, 7 d) under column <paramref name="x"/> of a chart
    /// <paramref name="w"/> wide, at the Paint scale. Returns <c>null</c> off the curve.
    /// </summary>
    /// <param name="pts">Window points sorted by time (period 0).</param>
    /// <param name="hist">History of the period (period &gt; 0).</param>
    /// <param name="period">Index into <see cref="Periods.All"/>.</param>
    /// <param name="bits">true = Mbps, false = MB/s.</param>
    public static GraphHit? GetHit(IReadOnlyList<LogBlock> pts, HistoryResult? hist, int period, double x, int w, DateTime now, bool bits)
    {
        if (period > 0)
        {
            if (hist is null || hist.Bytes <= 0) return null;
            var n = hist.Rates.Length;
            var i = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(x * n / w)));
            var step = Periods.At(period).Step;
            var a = hist.First.AddSeconds((double)i * step);
            var b = a.AddSeconds(step);
            if (b > now) b = now;
            return new GraphHit(
                (i + 0.5) * w / n,
                a.ToString("ddd d, HH':'mm", BzConstants.English) + " to " + b.ToString("HH':'mm", BzConstants.Invariant) + "  -  " +
                Formatting.FormatSize(hist.SlotBytes[i]) + "  -  average " + Formatting.FormatRate(hist.Rates[i], bits));
        }

        if (pts.Count == 0) return null;
        var window = (double)BzConstants.WindowSec;
        // Points sorted by time: binary search for the first at or after t
        var t0 = now.AddSeconds(-BzConstants.WindowSec);
        var t = t0.AddSeconds(x / w * window);
        int lo = 0, hi = pts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (pts[mid].Time < t) lo = mid + 1; else hi = mid;
        }

        var p = pts[lo];
        var idx = lo;
        if (lo > 0 && (t - pts[lo - 1].Time) < (p.Time - t))
        {
            p = pts[lo - 1];
            idx = lo - 1;
        }

        // The curve is continuous between two blocks with no gap, and after the last one while sending
        // continues. Anywhere else, further out, we are in a gap or it drops to zero
        bool run;
        if (pts[lo].Time >= t) run = lo > 0 && !RateCalculator.IsHole(pts[lo - 1].Time, pts[lo]);
        else run = RateCalculator.IsSending(pts[lo], now);
        if (!run && Math.Abs((p.Time - t).TotalSeconds) > BzConstants.GapSec / 2.0) return null;
        return new GraphHit(
            (p.Time - t0).TotalSeconds / window * w,
            p.Time.ToString("HH':'mm':'ss", BzConstants.Invariant) + "  -  " + Formatting.FormatRate(RateCalculator.GetInFlightRate(pts, idx), bits) + "  -  " +
            Formatting.FormatSize(p.Bytes) + "\r\n" + p.Name);
    }
}
