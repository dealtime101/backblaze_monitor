namespace BackblazeMonitor.Core;

/// <summary>Current rate, "is it sending" and gap detection over the window points.</summary>
public static class RateCalculator
{
    /// <summary>
    /// Current rate in bits/s. The log gives the rate of ONE block, ONE thread, and Backblaze runs several
    /// threads: the bytes completed within the last 2 minutes tell the truth. Nothing completed but a
    /// block that can still be in flight (see <see cref="IsSending"/>): keep the last block's rate rather than
    /// zero (a single slow thread would otherwise drop to zero between two lines). Otherwise sending has
    /// stopped and the rate is zero.
    /// </summary>
    /// <param name="points">Window points, sorted by time.</param>
    public static double GetCurrentRate(IReadOnlyList<LogBlock> points, DateTime now)
    {
        var cut = now.AddSeconds(-BzConstants.RateSec);
        var octets = 0.0;
        foreach (var p in points)
        {
            if (p.Time >= cut) octets += p.Bytes;
        }

        if (octets > 0) return 8 * octets / BzConstants.RateSec;
        if (points.Count > 0 && IsSending(points[^1], now)) return points[^1].Bits;
        return 0.0;
    }

    /// <summary>
    /// TOTAL rate at the moment block <paramref name="i"/> ends: the sum of the rates of the blocks in flight
    /// then (start &lt;= t &lt;= end). The log gives the rate of ONE block of ONE thread; the curve and the peak
    /// took it as is and stayed under the current rate (measured on 10,505 real blocks: median 6.3 against
    /// 19.6 Mbps; the sum of the blocks in flight matches the current rate, median ratio 1.00). A block alone
    /// keeps its own rate (BAC466.28).
    /// </summary>
    /// <param name="pts">Window points, sorted by time.</param>
    /// <param name="maxDur">Longest in-flight time among the points, which bounds the scan; negative = compute it.</param>
    public static double GetInFlightRate(IReadOnlyList<LogBlock> pts, int i, double maxDur = -1)
    {
        if (maxDur < 0) maxDur = MaxInFlight(pts);
        var own = pts[i].Bits;
        var t = pts[i].Time;
        while (i > 0 && pts[i - 1].Time == t) i--; // same second: they are all in flight for each other
        var sum = 0.0;
        for (var j = i; j < pts.Count; j++)
        {
            var q = pts[j];
            var dt = (q.Time - t).TotalSeconds;
            if (dt > maxDur) break; // the next blocks end too late to have started before t
            if (q.Bytes > 0 && q.Bits > 0 && dt <= q.InFlightSeconds) sum += q.Bits;
        }

        return sum > 0 ? sum : own;
    }

    /// <summary>Total rate (<see cref="GetInFlightRate"/>) at the end of each point.</summary>
    public static double[] GetRateSeries(IReadOnlyList<LogBlock> pts)
    {
        var r = new double[pts.Count];
        if (r.Length == 0) return r;
        var maxDur = MaxInFlight(pts);
        for (var i = 0; i < r.Length; i++) r[i] = GetInFlightRate(pts, i, maxDur);
        return r;
    }

    private static double MaxInFlight(IReadOnlyList<LogBlock> pts)
    {
        var max = 0.0;
        foreach (var q in pts)
        {
            if (q.Bytes > 0 && q.Bits > 0 && q.InFlightSeconds > max) max = q.InFlightSeconds;
        }

        return max;
    }

    /// <summary>
    /// Sending is in progress as long as the next line may still arrive: <see cref="BzConstants.IdleSec"/>,
    /// or twice the in-flight time of the last block.
    /// </summary>
    public static bool IsSending(LogBlock last, DateTime now) =>
        (now - last.Time).TotalSeconds <= Math.Max(BzConstants.IdleSec, 2 * last.InFlightSeconds);

    /// <summary>
    /// Gap in transmissions: the interval between two lines, excluding the in-flight time of the following
    /// block, exceeds <see cref="BzConstants.GapSec"/>.
    /// </summary>
    public static bool IsHole(DateTime prevTime, LogBlock p) =>
        (p.Time - prevTime).TotalSeconds - p.InFlightSeconds > BzConstants.GapSec;
}
