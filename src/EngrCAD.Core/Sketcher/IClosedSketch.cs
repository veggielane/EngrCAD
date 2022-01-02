using System;
using System.Collections.Generic;
using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Numerics;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public interface IClosedSketch
{
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }


    Node Extrude(float distance);
    Node Revolve(IAxis axis);
    Node Revolve(IAxis axis, Angle angle);

    /// <summary>
    /// Loft current sketch to one or more other sketches
    /// </summary>
    /// <example>
    ///  sketch.Loft(otherSketch);
    ///  sketch.Loft(otherSketchA, otherSketchB);
    /// </example>
    /// <param name="sketches">other sketches to loft through</param>
    /// <returns>Solid created by lofting sketches</returns>
    Node Loft(params IClosedSketch[] sketches);

    /// <summary>
    /// Loft current sketch to one or more other sketches
    /// </summary>
    /// <example>
    ///  sketch.Loft(new List&lt;IClosedSketch&gt;(){otherSketchA, otherSketchB});
    /// </example>
    /// <param name="sketches">other sketches to loft through</param>
    /// <returns>Solid created by lofting sketches</returns>
    Node Loft(IEnumerable<IClosedSketch> sketches);
}