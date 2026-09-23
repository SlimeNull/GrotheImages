using System;
using System.Globalization;

namespace GrotheImages;

public readonly record struct TransformMatrix(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static TransformMatrix Identity => new TransformMatrix(
        1, 0, 0,
        0, 1, 0,
        0, 0, 1);

    public bool IsFinite
    {
        get
        {
            return IsFiniteValue(M00) && IsFiniteValue(M01) && IsFiniteValue(M02)
                && IsFiniteValue(M10) && IsFiniteValue(M11) && IsFiniteValue(M12)
                && IsFiniteValue(M20) && IsFiniteValue(M21) && IsFiniteValue(M22);
        }
    }

    public bool TryInvert(out TransformMatrix inverse)
    {
        double c00 = M11 * M22 - M12 * M21;
        double c01 = M02 * M21 - M01 * M22;
        double c02 = M01 * M12 - M02 * M11;
        double c10 = M12 * M20 - M10 * M22;
        double c11 = M00 * M22 - M02 * M20;
        double c12 = M02 * M10 - M00 * M12;
        double c20 = M10 * M21 - M11 * M20;
        double c21 = M01 * M20 - M00 * M21;
        double c22 = M00 * M11 - M01 * M10;
        double determinant = M00 * c00 + M01 * c10 + M02 * c20;

        if (!IsFinite || !IsFiniteValue(determinant) || Math.Abs(determinant) < 1e-15)
        {
            inverse = default(TransformMatrix);
            return false;
        }

        double inv = 1.0 / determinant;
        inverse = new TransformMatrix(
            c00 * inv, c01 * inv, c02 * inv,
            c10 * inv, c11 * inv, c12 * inv,
            c20 * inv, c21 * inv, c22 * inv);
        return inverse.IsFinite;
    }

    public (double X, double Y) TransformPoint(double x, double y)
    {
        double w = M20 * x + M21 * y + M22;
        double tx = M00 * x + M01 * y + M02;
        double ty = M10 * x + M11 * y + M12;
        if (!IsFiniteValue(w) || Math.Abs(w) < 1e-15)
            throw new InvalidOperationException("The transformed homogeneous coordinate is invalid.");
        return (tx / w, ty / w);
    }

    internal float[] ToFloatArray()
    {
        if (!IsFinite)
            throw new ArgumentException("The transform matrix must contain only finite values.", nameof(TransformMatrix));
        return new[]
        {
            (float)M00, (float)M01, (float)M02,
            (float)M10, (float)M11, (float)M12,
            (float)M20, (float)M21, (float)M22,
        };
    }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "[{0}, {1}, {2}; {3}, {4}, {5}; {6}, {7}, {8}]",
            M00, M01, M02, M10, M11, M12, M20, M21, M22);
    }

    private static bool IsFiniteValue(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
