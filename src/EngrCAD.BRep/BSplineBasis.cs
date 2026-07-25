namespace EngrCAD.BRep;

/// <summary>
/// B-spline basis function evaluation (The NURBS Book, algorithms A2.1/A2.2/A2.3).
/// </summary>
/// <remarks>
/// The basis depends only on the knot vector, the degree and the parameter — never on
/// the control points — so it is shared verbatim by <see cref="NurbsCurve"/>,
/// <see cref="NurbsCurve2d"/> and <see cref="NurbsSurface"/>. Deliberately NOT forked
/// per dimension: only the control-point accumulation differs between them, and a
/// second copy of A2.3 would be a second place for the derivative recurrence to be
/// wrong. All methods write into caller-supplied <see cref="Span{T}"/> buffers so hot
/// evaluation paths can <c>stackalloc</c> and allocate nothing.
/// </remarks>
public static class BSplineBasis
{
    /// <summary>
    /// Index of the knot span containing <paramref name="u"/> (algorithm A2.1), clamped
    /// to the valid range [degree, controlPointCount − 1].
    /// </summary>
    public static int FindSpan(double u, int degree, int controlPointCount, IReadOnlyList<double> knots)
    {
        int n = controlPointCount - 1;
        if (u >= knots[n + 1])
            return n;
        if (u <= knots[degree])
            return degree;
        int low = degree, high = n + 1;
        int mid = (low + high) / 2;
        while (u < knots[mid] || u >= knots[mid + 1])
        {
            if (u < knots[mid])
                high = mid;
            else
                low = mid;
            mid = (low + high) / 2;
        }
        return mid;
    }

    /// <summary>
    /// Basis functions and their derivatives up to <paramref name="order"/> (algorithm
    /// A2.3, DersBasisFuns). <paramref name="ders"/> is (order + 1) × (degree + 1)
    /// row-major: ders[k * (degree + 1) + j] is the k-th derivative of basis function
    /// span − degree + j at u. Derivatives of order above the degree are zero.
    /// </summary>
    public static void EvaluateDerivatives(int span, double u, int degree, IReadOnlyList<double> knots, int order, Span<double> ders)
    {
        int p = degree;
        int stride = p + 1;
        // ndu upper triangle holds basis values N_r^j, lower triangle the knot differences.
        Span<double> ndu = stackalloc double[stride * stride];
        Span<double> left = stackalloc double[stride];
        Span<double> right = stackalloc double[stride];
        Span<double> a = stackalloc double[2 * stride];

        ndu[0] = 1.0;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                ndu[j * stride + r] = right[r + 1] + left[j - r];
                double temp = ndu[r * stride + j - 1] / ndu[j * stride + r];
                ndu[r * stride + j] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            ndu[j * stride + j] = saved;
        }

        for (int j = 0; j <= p; j++)
            ders[j] = ndu[j * stride + p];
        int maxOrder = Math.Min(order, p);
        for (int k = p + 1; k <= order; k++)
        {
            for (int j = 0; j <= p; j++)
                ders[k * stride + j] = 0;
        }

        for (int r = 0; r <= p; r++)
        {
            int s1 = 0, s2 = 1;
            a[0] = 1.0;
            for (int k = 1; k <= maxOrder; k++)
            {
                double d = 0;
                int rk = r - k, pk = p - k;
                if (r >= k)
                {
                    a[s2 * stride] = a[s1 * stride] / ndu[(pk + 1) * stride + rk];
                    d = a[s2 * stride] * ndu[rk * stride + pk];
                }
                int j1 = rk >= -1 ? 1 : -rk;
                int j2 = r - 1 <= pk ? k - 1 : p - r;
                for (int j = j1; j <= j2; j++)
                {
                    a[s2 * stride + j] = (a[s1 * stride + j] - a[s1 * stride + j - 1]) / ndu[(pk + 1) * stride + rk + j];
                    d += a[s2 * stride + j] * ndu[(rk + j) * stride + pk];
                }
                if (r <= pk)
                {
                    a[s2 * stride + k] = -a[s1 * stride + k - 1] / ndu[(pk + 1) * stride + r];
                    d += a[s2 * stride + k] * ndu[r * stride + pk];
                }
                ders[k * stride + r] = d;
                (s1, s2) = (s2, s1);
            }
        }

        double factor = p;
        for (int k = 1; k <= maxOrder; k++)
        {
            for (int j = 0; j <= p; j++)
                ders[k * stride + j] *= factor;
            factor *= p - k;
        }
    }

    /// <summary>
    /// The degree + 1 nonzero basis functions at <paramref name="u"/> in the given knot
    /// span (algorithm A2.2); <paramref name="basis"/>[j] belongs to control point
    /// span − degree + j.
    /// </summary>
    public static void Evaluate(int span, double u, int degree, IReadOnlyList<double> knots, Span<double> basis)
    {
        Span<double> left = stackalloc double[degree + 1];
        Span<double> right = stackalloc double[degree + 1];
        basis[0] = 1;
        for (int j = 1; j <= degree; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                double temp = basis[r] / (right[r + 1] + left[j - r]);
                basis[r] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            basis[j] = saved;
        }
    }
}
