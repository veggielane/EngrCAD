using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// <c>gvar</c>: per-glyph outline deltas across a variable font's design space, plus
/// the interpolation (IUP) that completes them.
/// <para><b>A tuple's deltas cover only the points it names.</b> A font states deltas for
/// the points that genuinely move — a stem's corners — and every other point of the
/// contour is INFERRED from its touched neighbours, which is what keeps a variable
/// font's data small and is where implementations go wrong. The rule is per contour, per
/// axis: an untouched point between two touched neighbours moves by the linear
/// interpolation of their deltas in that axis's own coordinate, and a point OUTSIDE the
/// two neighbours' coordinate range is TRANSLATED by the nearer neighbour's delta rather
/// than extrapolated. A contour with exactly one touched point translates entirely; one
/// with none does not move.</para>
/// <para><b>Phantom points.</b> Every glyph's point list is extended by four points the
/// outline does not contain — left side bearing, advance width, top side bearing,
/// advance height — so a font can vary its METRICS with the same machinery it varies its
/// outline. The advance-width phantom is what makes a bolder instance lay out at bolder
/// spacing; see <see cref="TrueTypeFont"/> for the <c>HVAR</c> route that supersedes it.
/// Each phantom is its own one-point contour for interpolation, so an untouched phantom
/// simply does not move.</para>
/// <para><b>Composite glyphs vary too</b>, and what varies is the COMPONENT OFFSETS
/// rather than any outline: a composite's point list is one point per component (its
/// placement) plus the same four phantoms, each component its own one-point contour.</para>
/// </summary>
internal sealed class GlyphVariationStore
{
    // tupleVariationCount flags
    private const int SharedPointNumbers = 0x8000;
    private const int TupleCountMask = 0x0FFF;

    // tupleIndex flags
    private const int EmbeddedPeakTuple = 0x8000;
    private const int IntermediateRegion = 0x4000;
    private const int PrivatePointNumbers = 0x2000;
    private const int TupleIndexMask = 0x0FFF;

    private readonly byte[] _data;
    private readonly int _axisCount;
    private readonly int[] _offsets;                     // absolute, glyphCount + 1 entries
    private readonly double[][] _sharedTuples;

    private GlyphVariationStore(byte[] data, int axisCount, int[] offsets, double[][] sharedTuples)
    {
        _data = data;
        _axisCount = axisCount;
        _offsets = offsets;
        _sharedTuples = sharedTuples;
    }

    public static GlyphVariationStore Read(byte[] data, int offset, int axisCount, int glyphCount)
    {
        var span = data.AsSpan();
        var reader = new FontReader(span, offset);
        int major = reader.ReadUInt16();
        reader.Skip(2);                                  // minorVersion
        if (major != 1)
            throw new FontFormatException($"gvar table version is {major}; only version 1 is defined.");
        int gvarAxisCount = reader.ReadUInt16();
        if (gvarAxisCount != axisCount)
            throw new FontFormatException(
                $"gvar declares {gvarAxisCount} axes but fvar declares {axisCount}.");
        int sharedTupleCount = reader.ReadUInt16();
        int sharedTuplesOffset = (int)reader.ReadUInt32();
        int gvarGlyphCount = reader.ReadUInt16();
        int flags = reader.ReadUInt16();
        int dataArrayOffset = (int)reader.ReadUInt32();
        if (gvarGlyphCount > glyphCount)
            throw new FontFormatException(
                $"gvar covers {gvarGlyphCount} glyphs but the font has {glyphCount}.");

        bool longOffsets = (flags & 0x0001) != 0;
        var offsets = new int[glyphCount + 1];
        for (int i = 0; i <= gvarGlyphCount; i++)
            offsets[i] = offset + dataArrayOffset + (longOffsets ? (int)reader.ReadUInt32() : reader.ReadUInt16() * 2);
        // A gvar covering fewer glyphs than the font is legal: the rest simply do not vary.
        for (int i = gvarGlyphCount + 1; i <= glyphCount; i++)
            offsets[i] = offsets[gvarGlyphCount];

        var sharedTuples = new double[sharedTupleCount][];
        var shared = new FontReader(span, offset + sharedTuplesOffset);
        for (int t = 0; t < sharedTupleCount; t++)
        {
            var tuple = new double[axisCount];
            for (int a = 0; a < axisCount; a++)
                tuple[a] = shared.ReadF2Dot14();
            sharedTuples[t] = tuple;
        }

        return new GlyphVariationStore(data, axisCount, offsets, sharedTuples);
    }

    /// <summary>
    /// The total displacement of every point of one glyph at
    /// <paramref name="coordinates"/>, in font units.
    /// </summary>
    /// <param name="glyphIndex">Glyph to read.</param>
    /// <param name="points">The glyph's points followed by four phantom points — real
    /// outline points for a simple glyph, one point per component for a composite. The
    /// phantoms' own coordinates are never read (each is its own one-point contour, so
    /// nothing interpolates through them).</param>
    /// <param name="contourEnds">Index of the last point of each contour — the real
    /// contours of a simple glyph, or one entry per component for a composite.</param>
    /// <param name="coordinates">Normalized axis coordinates.</param>
    /// <returns>One delta per entry of <paramref name="points"/>, or null when the glyph
    /// carries no variation data at all (the caller then moves nothing).</returns>
    public Vector2d[]? Deltas(int glyphIndex, IReadOnlyList<Vector2d> points, IReadOnlyList<int> contourEnds,
        ReadOnlySpan<double> coordinates)
    {
        if ((uint)glyphIndex >= (uint)(_offsets.Length - 1))
            return null;
        int start = _offsets[glyphIndex], end = _offsets[glyphIndex + 1];
        if (end <= start)
            return null;                                 // this glyph does not vary

        int totalPoints = points.Count;
        var span = _data.AsSpan();
        var reader = new FontReader(span, start);
        int tupleVariationCount = reader.ReadUInt16();
        int serializedOffset = reader.ReadUInt16();
        int tupleCount = tupleVariationCount & TupleCountMask;
        if (tupleCount == 0)
            return null;

        // Tuple headers run from here; the serialized data they index sits at the
        // table-relative dataOffset, shared point numbers (when declared) first.
        var headers = new (int Size, int Index, double[] Peak, double[]? IntermediateStart, double[]? IntermediateEnd)[tupleCount];
        for (int t = 0; t < tupleCount; t++)
        {
            int size = reader.ReadUInt16();
            int index = reader.ReadUInt16();
            double[] peak;
            if ((index & EmbeddedPeakTuple) != 0)
            {
                peak = new double[_axisCount];
                for (int a = 0; a < _axisCount; a++)
                    peak[a] = reader.ReadF2Dot14();
            }
            else
            {
                int shared = index & TupleIndexMask;
                if (shared >= _sharedTuples.Length)
                    throw new FontFormatException(
                        $"Glyph {glyphIndex}: gvar tuple {t} names shared tuple {shared}, past the {_sharedTuples.Length} defined.");
                peak = _sharedTuples[shared];
            }
            double[]? intermediateStart = null, intermediateEnd = null;
            if ((index & IntermediateRegion) != 0)
            {
                intermediateStart = new double[_axisCount];
                for (int a = 0; a < _axisCount; a++)
                    intermediateStart[a] = reader.ReadF2Dot14();
                intermediateEnd = new double[_axisCount];
                for (int a = 0; a < _axisCount; a++)
                    intermediateEnd[a] = reader.ReadF2Dot14();
            }
            headers[t] = (size, index, peak, intermediateStart, intermediateEnd);
        }

        int at = start + serializedOffset;
        int[]? sharedPoints = null;
        if ((tupleVariationCount & SharedPointNumbers) != 0)
        {
            var pointReader = new FontReader(span, at);
            sharedPoints = ReadPackedPointNumbers(ref pointReader);
            at = pointReader.Position;
        }

        Vector2d[]? total = null;
        var scratchX = new double[totalPoints];
        var scratchY = new double[totalPoints];
        var touched = new bool[totalPoints];
        foreach (var header in headers)
        {
            int tupleData = at;
            at += header.Size;

            var region = Region(header.Peak, header.IntermediateStart, header.IntermediateEnd);
            double scalar = region.Scalar(coordinates);
            if (scalar == 0)
                continue;                                // still advanced past its data, which is the point

            var tupleReader = new FontReader(span, tupleData);
            int[]? numbers = (header.Index & PrivatePointNumbers) != 0
                ? ReadPackedPointNumbers(ref tupleReader)
                : sharedPoints;

            int deltaCount = numbers?.Length ?? totalPoints;
            var deltaX = ReadPackedDeltas(ref tupleReader, deltaCount);
            var deltaY = ReadPackedDeltas(ref tupleReader, deltaCount);

            Array.Clear(scratchX);
            Array.Clear(scratchY);
            if (numbers is null)
            {
                for (int i = 0; i < totalPoints; i++)
                {
                    scratchX[i] = deltaX[i];
                    scratchY[i] = deltaY[i];
                }
            }
            else
            {
                Array.Clear(touched);
                for (int i = 0; i < numbers.Length; i++)
                {
                    int point = numbers[i];
                    if ((uint)point >= (uint)totalPoints)
                        throw new FontFormatException(
                            $"Glyph {glyphIndex}: gvar names point {point}, past the {totalPoints} points (outline plus four phantoms).");
                    scratchX[point] = deltaX[i];
                    scratchY[point] = deltaY[i];
                    touched[point] = true;
                }
                Interpolate(scratchX, scratchY, touched, points, contourEnds);
            }

            total ??= new Vector2d[totalPoints];
            for (int i = 0; i < totalPoints; i++)
                total[i] += new Vector2d(scalar * scratchX[i], scalar * scratchY[i]);
        }
        return total;
    }

    /// <summary>A tuple's region: an explicit intermediate triple when the header
    /// carries one, otherwise the implied one-sided region from the default to the peak
    /// (which is what makes a plain peak tuple ramp linearly out of the default).</summary>
    private VariationRegion Region(double[] peak, double[]? intermediateStart, double[]? intermediateEnd)
    {
        if (intermediateStart is not null && intermediateEnd is not null)
            return new VariationRegion(intermediateStart, peak, intermediateEnd);
        var start = new double[_axisCount];
        var end = new double[_axisCount];
        for (int a = 0; a < _axisCount; a++)
        {
            start[a] = Math.Min(peak[a], 0);
            end[a] = Math.Max(peak[a], 0);
        }
        return new VariationRegion(start, peak, end);
    }

    // ---- IUP -----------------------------------------------------------------

    /// <summary>
    /// Inferred Unreferenced Points: completes a tuple's deltas over every contour, the
    /// four phantom points each counting as their own one-point contour (so an untouched
    /// phantom does not move — which is exactly what "this tuple does not change the
    /// advance" means).
    /// </summary>
    private static void Interpolate(double[] deltaX, double[] deltaY, bool[] touched,
        IReadOnlyList<Vector2d> points, IReadOnlyList<int> contourEnds)
    {
        int start = 0;
        foreach (int end in contourEnds)
        {
            if (end >= start)
                InterpolateContour(deltaX, deltaY, touched, points, start, end);
            start = end + 1;
        }
        // The four phantom points past the last contour are one-point contours, so there
        // is nothing to infer for them: an untouched phantom simply does not move.
    }

    private static void InterpolateContour(double[] deltaX, double[] deltaY, bool[] touched,
        IReadOnlyList<Vector2d> points, int start, int end)
    {
        int count = end - start + 1;
        var anchors = new List<int>();
        for (int i = start; i <= end; i++)
        {
            if (touched[i])
                anchors.Add(i);
        }
        if (anchors.Count == 0 || anchors.Count == count)
            return;                                      // nothing moves, or nothing to infer
        if (anchors.Count == 1)
        {
            // One touched point translates the whole contour: there is no second
            // reference to interpolate against, and leaving the rest still would tear it.
            int only = anchors[0];
            for (int i = start; i <= end; i++)
            {
                deltaX[i] = deltaX[only];
                deltaY[i] = deltaY[only];
            }
            return;
        }

        for (int k = 0; k < anchors.Count; k++)
            FillGap(deltaX, deltaY, points, start, count, anchors[k], anchors[(k + 1) % anchors.Count]);
    }

    /// <summary>Fills the points strictly between two touched anchors, walking forward
    /// cyclically within the contour.</summary>
    private static void FillGap(double[] deltaX, double[] deltaY, IReadOnlyList<Vector2d> points,
        int start, int count, int anchorA, int anchorB)
    {
        for (int step = 1; ; step++)
        {
            int i = start + ((anchorA - start + step) % count);
            if (i == anchorB)
                return;
            deltaX[i] = Segment(points[i].X, points[anchorA].X, deltaX[anchorA], points[anchorB].X, deltaX[anchorB]);
            deltaY[i] = Segment(points[i].Y, points[anchorA].Y, deltaY[anchorA], points[anchorB].Y, deltaY[anchorB]);
        }
    }

    /// <summary>
    /// One axis of the interpolation rule: between the two anchors' coordinates the
    /// delta is their linear interpolation; OUTSIDE that range the point is TRANSLATED by
    /// the nearer anchor's delta rather than extrapolated (the clause that keeps a
    /// contour from tearing itself apart past its own touched span). Two anchors at the
    /// same coordinate carry no direction, so they translate when they agree and move
    /// nothing when they do not.
    /// </summary>
    private static double Segment(double coordinate, double a, double deltaA, double b, double deltaB)
    {
        if (a > b)
        {
            (a, b) = (b, a);
            (deltaA, deltaB) = (deltaB, deltaA);
        }
        if (a == b)
            return deltaA == deltaB ? deltaA : 0;
        if (coordinate <= a)
            return deltaA;
        if (coordinate >= b)
            return deltaB;
        return deltaA + (deltaB - deltaA) * (coordinate - a) / (b - a);
    }

    // ---- packed encodings ----------------------------------------------------

    /// <summary>Packed point numbers: a count (one or two bytes) then runs of deltas
    /// from the previous number. A count of zero means ALL points, spelled as null.</summary>
    private static int[]? ReadPackedPointNumbers(ref FontReader reader)
    {
        int count = reader.ReadUInt8();
        if ((count & 0x80) != 0)
            count = ((count & 0x7F) << 8) | reader.ReadUInt8();
        if (count == 0)
            return null;                                 // "all points", including the phantoms

        var numbers = new int[count];
        int written = 0, previous = 0;
        while (written < count)
        {
            int control = reader.ReadUInt8();
            int run = (control & 0x7F) + 1;
            bool words = (control & 0x80) != 0;
            for (int i = 0; i < run && written < count; i++)
            {
                previous += words ? reader.ReadUInt16() : reader.ReadUInt8();
                numbers[written++] = previous;
            }
        }
        return numbers;
    }

    /// <summary>Packed deltas: runs of zeros, signed bytes or signed words.</summary>
    private static int[] ReadPackedDeltas(ref FontReader reader, int count)
    {
        var deltas = new int[count];
        int written = 0;
        while (written < count)
        {
            int control = reader.ReadUInt8();
            int run = (control & 0x3F) + 1;
            if ((control & 0x80) != 0)                   // DELTAS_ARE_ZERO wins over the width bit
            {
                written = Math.Min(count, written + run);
                continue;
            }
            bool words = (control & 0x40) != 0;
            for (int i = 0; i < run && written < count; i++)
                deltas[written++] = words ? reader.ReadInt16() : reader.ReadInt8();
        }
        return deltas;
    }
}
