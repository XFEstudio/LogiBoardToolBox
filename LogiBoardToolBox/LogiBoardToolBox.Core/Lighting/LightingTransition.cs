namespace LogiBoardToolBox.Core.Lighting;

public enum TransitionCurve { Linear, InOutQuad, OutInQuad, InOutCubic, OutCirc, InCirc, OutInCubic, InOutCirc, OutInBack, None }
public enum AnimationDirection { Cycle, ReverseCycle, Bounce, Random }

public static class LightingTransition
{
    public static double Evaluate(TransitionCurve curve, double progress)
    {
        var x = Math.Clamp(progress, 0, 1);
        if (curve == TransitionCurve.None) return 0;
        if (curve == TransitionCurve.Linear) return x;
        var (x1, y1, x2, y2) = curve switch
        {
            TransitionCurve.InOutQuad => (.45, 0d, .55, 1d), TransitionCurve.OutInQuad => (.5, 1d, .5, 0d),
            TransitionCurve.InOutCubic => (.65, 0d, .35, 1d), TransitionCurve.OutCirc => (0d, .55, .45, 1d),
            TransitionCurve.InCirc => (.55, 0d, 1d, .45), TransitionCurve.OutInCubic => (.3, 1d, .7, 0d),
            TransitionCurve.InOutCirc => (.85, 0d, .15, 1d), TransitionCurve.OutInBack => (.25, 1.3, .75, -.3),
            _ => throw new ArgumentOutOfRangeException(nameof(curve))
        };
        static double Bezier(double t, double a, double b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
        double lo = 0, hi = 1;
        for (var n = 0; n < 24; n++) { var mid = (lo + hi) / 2; if (Bezier(mid, x1, x2) < x) lo = mid; else hi = mid; }
        return Math.Clamp(Bezier((lo + hi) / 2, y1, y2), 0, 1);
    }
}
