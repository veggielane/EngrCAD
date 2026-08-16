using EngrCAD.Core;

namespace EngrCAD.Viewer;

// The shared render core: shader sources, camera/projection math, and scene-furniture
// geometry used by BOTH render paths — the interactive ViewportControl and the headless
// OffscreenRenderer. Before this file existed the two deliberately duplicated ~150
// lines and drifted silently (the offscreen pass gained a scene-scaled frustum the
// window never got); anything look- or framing-related belongs here so a change lands
// in both passes at once.
//
// It lives in EngrCAD.Viewer.Core — no Avalonia, no Silk.NET — so a THIRD front end (the
// Blazor WebAssembly client, whose GL is WebGL2 through JS interop) shares this code
// rather than copying it, which is the same drift the file was created to stop. Anything
// that takes a `GL` stays in EngrCAD.Viewer (RenderCore.cs there).

/// <summary>
/// Global view style for a whole render pass — the classic CAD view-style dropdown
/// (points / wireframe / shaded / shaded with edges). The global style decides how
/// parts render *by default*; a part whose <see cref="EngrCAD.Modeling.Part.DisplayMode"/>
/// is explicitly non-default (Wireframe or Translucent) overrides it for that part.
/// <c>DisplayMode.Shaded</c> IS the default, so it cannot override — parts left at the
/// default follow the global style. <see cref="ShadedWithEdges"/> is the traditional
/// default look; <see cref="Shaded"/> is the same fill without the feature-edge
/// overlay.
/// </summary>
public enum ViewStyle
{
    /// <summary>Vertex point sprites only (mesh density inspection).</summary>
    Points,

    /// <summary>Every mesh edge as a line, no fills ("mesh" view).</summary>
    Wireframe,

    /// <summary>Lit fills without the feature-edge overlay.</summary>
    Shaded,

    /// <summary>Lit fills with the feature-edge overlay (the default CAD look).</summary>
    ShadedWithEdges,
}

/// <summary>
/// How a FILL is lit, for a whole render pass — deliberately not a
/// <see cref="ViewStyle"/> member, because the style is about what is drawn (points,
/// lines, fills) while shading is about how a fill is lit, and the two compose (a
/// matcap-shaded translucent part is legal). The non-default members are <b>analytic
/// matcaps</b>: the lit-sphere image a matcap samples is procedural — Gaussian lobes
/// over the view-space normal, evaluated in <c>ViewerShaders.MeshFragment</c> — so no
/// texture machinery has to reach three front ends, and the material constants live in
/// the one shader file window, offscreen and web already share. Global per pass, with
/// no per-part override in v1: a scene lit two ways reads as a rendering bug.
/// <para>The numeric values are the shader's <c>uMatcap</c> selector verbatim, and
/// <see cref="Lit"/> is 0 — the value a linked program's uniforms initialize to — so a
/// front end that says nothing renders exactly the incumbent look.</para>
/// </summary>
public enum ShadingStyle
{
    /// <summary>The standard directional light + specular (the default look).</summary>
    Lit = 0,

    /// <summary>Matte studio matcap: a broad key lobe and a soft fill.</summary>
    Clay = 1,

    /// <summary>Polished-metal matcap: dark base, tight highlight, bright rim.</summary>
    Metal = 2,
}

/// <summary>
/// How the 3D-annotation (PMI) overlay treats model geometry standing in front of it —
/// a whole-pass choice like <see cref="ShadingStyle"/>, shared by the window, the
/// headless renderer and the browser client so a dimension cannot behave differently
/// between them.
/// <para>The numeric values matter the way <c>ShadingStyle.Lit = 0</c> does:
/// <see cref="AlwaysOnTop"/> is 0, so a front end that says nothing renders the
/// incumbent overlay.</para>
/// </summary>
public enum AnnotationDepth
{
    /// <summary>
    /// The whole overlay is drawn with the depth test OFF, so every dimension reads over
    /// the model from any angle. Nothing is ever hidden and nothing is ever ambiguous —
    /// which is why it stays the default — but the picture says nothing about which side
    /// of the part a dimension is on.
    /// </summary>
    AlwaysOnTop = 0,

    /// <summary>
    /// Depth-tested: stretches with material in front of them are drawn <b>dimmed</b>
    /// (<see cref="AnnotationGeometry.HiddenColor"/>) instead of at full strength, the
    /// drafting-honest reading — a dimension on the far side of the part recedes.
    /// <para>Nothing disappears, which is the load-bearing property: the overlay is
    /// still complete, so picking stays depth-blind (an annotation you can SEE is
    /// pickable) and no dimension can be lost behind a boss.</para>
    /// </summary>
    Occluded = 1,
}

/// <summary>
/// Which world axis a section plane is perpendicular to (v1 restricts section planes
/// to the three world axes). The plane keeps everything at
/// <c>coordinate &lt;= offset</c> along the axis and clips what lies above.
/// </summary>
public enum SectionAxis
{
    /// <summary>Plane x = offset; clips world +X.</summary>
    X,

    /// <summary>Plane y = offset; clips world +Y.</summary>
    Y,

    /// <summary>Plane z = offset; clips world +Z (the classic horizontal section).</summary>
    Z,
}

/// <summary>Extensions for <see cref="SectionAxis"/> shared by both render passes.</summary>
public static class SectionAxisExtensions
{
    /// <summary>The world-space unit vector of the axis (the plane's normal).</summary>
    public static Vector3d Direction(this SectionAxis axis) => axis switch
    {
        SectionAxis.X => Vector3d.UnitX,
        SectionAxis.Y => Vector3d.UnitY,
        _ => Vector3d.UnitZ,
    };
}

/// <summary>
/// One section (clip) plane: geometry with <c>dot(world, Normal) &gt; Offset</c> is
/// <em>excluded</em> by this plane. Several planes at once make the classic CAD
/// cutaways — two perpendicular planes a quarter cut, three an octant — combined by
/// <see cref="SectionCombine"/>. The normal is a general direction (the shaders have
/// always taken one); <see cref="On(SectionAxis, double)"/> builds the axis-aligned
/// planes the toolbar and CLI expose.
/// </summary>
/// <param name="Normal">Plane normal, pointing at the clipped side. Need not be unit
/// length, but <paramref name="Offset"/> is measured in its units.</param>
/// <param name="Offset">Plane position: the value of <c>dot(world, Normal)</c> on it.</param>
public readonly record struct SectionPlane(Vector3d Normal, double Offset)
{
    /// <summary>An axis-aligned plane perpendicular to <paramref name="axis"/>.</summary>
    public static SectionPlane On(SectionAxis axis, double offset) => new(axis.Direction(), offset);

    /// <summary>
    /// A plane placed by a rigid frame: the frame's origin lies ON the section plane and
    /// its <b>Z axis points at the clipped side</b>. This is how a cut gets an arbitrary
    /// orientation — everything downstream (the shaders, <see cref="SectionClip"/>, the
    /// isoline overlay) has always taken a general normal; only the toolbar's axis
    /// cycler is restricted to X/Y/Z. Pairs naturally with a sketch plane or a
    /// <c>BrepQueries.Frame(face)</c>, so a section can be taken ON a face.
    /// </summary>
    public static SectionPlane On(in Frame3d frame) => new(frame.Z, frame.Origin.Dot(frame.Z));

    /// <summary>A plane through <paramref name="point"/> whose <paramref name="normal"/>
    /// points at the clipped side (need not be unit length).</summary>
    public static SectionPlane Through(in Vector3d point, in Vector3d normal) =>
        new(normal, point.Dot(normal));

    /// <summary>The same plane with the kept and clipped sides swapped.</summary>
    public SectionPlane Flipped() => new(-Normal, -Offset);
}

/// <summary>
/// How several <see cref="SectionPlane"/>s combine into one cut.
/// </summary>
public enum SectionCombine
{
    /// <summary>
    /// Clip only where EVERY plane excludes — the CAD-standard cutaway: two
    /// perpendicular planes remove the quarter where both exclude, three remove an
    /// octant. The default.
    /// </summary>
    Intersection,

    /// <summary>
    /// Clip where ANY plane excludes — the single-plane behavior generalized; each
    /// plane cuts independently, so the model keeps only what every plane keeps.
    /// </summary>
    Union,
}

/// <summary>How one instance actually renders after combining the global
/// <see cref="ViewStyle"/> with the part's own <c>DisplayMode</c>.
/// <para>
/// Public because it crosses the assembly boundary: a front end asks
/// <see cref="RenderModes.Resolve"/> which of these five buckets an instance falls into
/// and picks its draw call accordingly (fills, lines, point sprites, alpha pass).
/// </para></summary>
public enum EffectiveMode
{
    /// <summary>Vertex point sprites.</summary>
    Points,

    /// <summary>Mesh edges as lines, no fills.</summary>
    Wireframe,

    /// <summary>Lit fills, no feature-edge overlay.</summary>
    Shaded,

    /// <summary>Lit fills plus the feature-edge overlay.</summary>
    ShadedWithEdges,

    /// <summary>Alpha-blended fills, drawn back-to-front.</summary>
    Translucent,
}

/// <summary>
/// Per-instance render-mode resolution and draw-order helpers shared by BOTH render
/// passes (window and offscreen), so precedence and translucency ordering cannot
/// drift between them.
/// <para>
/// A front end calls <see cref="Resolve"/> once per instance per frame to decide how to
/// draw it, and <see cref="SortBackToFront"/> to order the translucent ones — the two
/// rules that make a third front end look identical to the desktop viewer.
/// </para>
/// </summary>
public static class RenderModes
{
    /// <summary>
    /// The precedence rule, in one place: an explicit non-default part mode
    /// (Wireframe/Translucent) wins; parts at the default (Shaded) follow the global
    /// style.
    /// </summary>
    public static EffectiveMode Resolve(ViewStyle style, EngrCAD.Modeling.DisplayMode mode) => mode switch
    {
        EngrCAD.Modeling.DisplayMode.Wireframe => EffectiveMode.Wireframe,
        EngrCAD.Modeling.DisplayMode.Translucent => EffectiveMode.Translucent,
        _ => style switch
        {
            ViewStyle.Points => EffectiveMode.Points,
            ViewStyle.Wireframe => EffectiveMode.Wireframe,
            ViewStyle.Shaded => EffectiveMode.Shaded,
            _ => EffectiveMode.ShadedWithEdges,
        },
    };

    /// <summary>
    /// Sorts the first <paramref name="count"/> entries of <paramref name="order"/>
    /// descending by their <paramref name="depth"/> keys (farthest first — back-to-front
    /// alpha blending). Insertion sort over parallel arrays: no comparer delegate, no
    /// allocation, and the arrays can be reused frame to frame.
    /// </summary>
    public static void SortBackToFront(int[] order, double[] depth, int count)
    {
        for (int k = 1; k < count; k++)
        {
            int index = order[k];
            double key = depth[k];
            int j = k - 1;
            while (j >= 0 && depth[j] < key)
            {
                order[j + 1] = order[j];
                depth[j + 1] = depth[j];
                j--;
            }
            order[j + 1] = index;
            depth[j + 1] = key;
        }
    }
}

/// <summary>
/// Selection and hover highlighting, in one place: ONE shader knob does both states,
/// and line-drawn parts (wireframe, points) blend their own colour instead because they
/// have no fill for <c>uHighlight</c> to act on.
/// <para>
/// Shared because a second front end that re-typed "selection is gold" would be a second
/// definition of what selection LOOKS like — and the two would drift the first time
/// either was tuned. The browser client reads these values into its frame description;
/// the desktop passes them as uniforms.
/// </para>
/// </summary>
public static class Highlight
{
    /// <summary>Selection gold, the one colour selection is drawn in (fills blend
    /// toward it in the shader, lines and points are drawn in it outright).</summary>
    public static readonly (float R, float G, float B) Selection = (1.0f, 0.85f, 0.35f);

    /// <summary>The <c>uHighlight</c> value for the selected instance.</summary>
    public const float Selected = 1f;

    /// <summary>The <c>uHighlight</c> value for the hovered instance — the fainter
    /// pre-selection tint. A hovered SELECTED instance shows selection only.</summary>
    public const float Hovered = 0.35f;

    /// <summary>The <c>uHighlight</c> uniform for one instance, given the current
    /// selection and hover (−1 for none).</summary>
    public static float Strength(int index, int selected, int hovered) =>
        index >= 0 && index == selected ? Selected
        : index >= 0 && index == hovered ? Hovered
        : 0f;

    /// <summary>
    /// Line/point colour for one instance: selection gold wins outright, hover blends
    /// the part's own colour <see cref="Hovered"/> of the way toward it, and anything
    /// else keeps its colour. Wireframe and point parts have no fill, so this is the
    /// only place their highlight can appear.
    /// </summary>
    public static (float R, float G, float B) LineColor(
        int index, int selected, int hovered, (float R, float G, float B) color) =>
        index >= 0 && index == selected ? Selection
        : index >= 0 && index == hovered
            ? (color.R + (Selection.R - color.R) * Hovered,
               color.G + (Selection.G - color.G) * Hovered,
               color.B + (Selection.B - color.B) * Hovered)
            : color;
}

/// <summary>
/// GLSL sources shared by the window and offscreen passes.
/// There is ONE shader set: the mesh shader carries every feature the viewport needs
/// (selection highlight, section plane, translucency); a pass that wants none of them
/// sets the neutral uniforms (uHighlight 0, uSectionEnabled 0, uAlpha 1).
/// <para>
/// A front end takes these sources verbatim and compiles them with whatever GL binding
/// it has (Silk.NET on the desktop, WebGL2 through JS interop in the browser) —
/// <see cref="Header"/> already emits both an ES3 and a desktop 3.3 version header,
/// and WebGL2 wants the ES3 one. Compilation itself is NOT here, because it needs a
/// <c>GL</c>; see <c>ViewerPrograms</c> in EngrCAD.Viewer for the desktop side.
/// </para>
/// <para>
/// HARD-WON LESSON: shader source strings must stay PURE ASCII. An em dash in a
/// comment once made ANGLE's translator reject the whole shader; the compile exception
/// aborted OnOpenGlInit before the other programs were built and the entire viewport
/// rendered black.
/// </para>
/// </summary>
public static class ViewerShaders
{
    private const string EsHeader = "#version 300 es\nprecision highp float;\n";
    private const string DesktopHeader = "#version 330 core\n";

    /// <summary>Version header for the GL profile in use (ES3 via ANGLE, or desktop 3.3).</summary>
    public static string Header(bool es) => es ? EsHeader : DesktopHeader;

    /// <summary>Maximum simultaneous section planes (a plane per world axis is the
    /// deepest cut that means anything; the uniform array is sized to it).</summary>
    public const int MaxSectionPlanes = 4;

    /// <summary>
    /// The section-plane uniforms and clip rule, prepended verbatim to every fragment
    /// shader that clips (mesh, line, point) so all three cannot disagree.
    /// uSectionEnabled is the master switch a pass flips per draw group (scene
    /// furniture never clips); uSectionPlanes carries xyz = normal, w = offset for
    /// uSectionCount planes; uSectionUnion picks the combine rule — 0 clips only where
    /// EVERY plane excludes (the quarter/octant cutaway), 1 where ANY does. With one
    /// plane the two rules coincide, which is why single-plane output is unchanged.
    /// </summary>
    public const string SectionClip = """
        uniform float uSectionEnabled;
        uniform int uSectionCount;
        uniform vec4 uSectionPlanes[4];
        uniform float uSectionUnion;
        bool sectionClipped(vec3 worldPos)
        {
            if (uSectionEnabled < 0.5 || uSectionCount <= 0)
                return false;
            bool any = false;
            bool all = true;
            // Constant loop bound with an early break: the safe pattern for the GLES
            // translator, which is happiest when the trip count is statically known.
            for (int i = 0; i < 4; i++)
            {
                if (i >= uSectionCount)
                    break;
                bool excluded = dot(worldPos, uSectionPlanes[i].xyz) > uSectionPlanes[i].w;
                any = any || excluded;
                all = all && excluded;
            }
            return uSectionUnion > 0.5 ? any : all;
        }

        """;

    /// <summary>Lit mesh vertex shader (position + normal + baked occlusion + field
    /// colour + displacement, world-space lighting). aOcclusion is attribute 2,
    /// aFieldColor attribute 3 and the four deformation attributes 4-7; a mesh uploaded
    /// without a given buffer leaves that array disabled and reads the constant set at
    /// pass init (1.0, white, and zero respectively).
    /// <para><b>The deformation is the whole reason a result animation is cheap.</b> A
    /// displaced shape looks like new geometry, and the CPU used to build it as such —
    /// which put it off the matrices-only animation path, because every frame would have
    /// re-uploaded. Sending the displacement ONCE as a vertex attribute and applying
    /// <c>aPos + uDeformScale * aDeformOffset</c> here makes a whole result animation one
    /// float uniform per frame, with no buffer touched and picking's instance matrices
    /// untouched.</para>
    /// <para><b>The normal is exact, not carried over</b>, and the identity that makes
    /// that possible is worth stating: a triangle whose three vertices move linearly in s
    /// has edges <c>a + s*alpha</c> and <c>b + s*beta</c>, so its facet normal
    /// <c>(a + s*alpha) x (b + s*beta)</c> is EXACTLY QUADRATIC in s. Three coefficient
    /// vectors therefore reproduce the displaced facet normal at every scale — the same
    /// normal the CPU path recomputed — for one more attribute rather than a re-upload.
    /// Only the direction matters (the fragment shader normalizes), so the coefficients
    /// are scaled to keep float32 well conditioned, and an all-zero result means the
    /// displaced triangle collapsed: fall back to the source normal, which is what the
    /// CPU path did.</para></summary>
    public const string MeshVertex = """
        in vec3 aPos;
        in vec3 aNormal;
        in float aOcclusion;
        in vec3 aFieldColor;
        in vec3 aDeformOffset;
        in vec3 aDeformNormal0;
        in vec3 aDeformNormal1;
        in vec3 aDeformNormal2;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform float uDeformScale;
        out vec3 vNormal;
        out vec3 vWorldPos;
        out float vOcclusion;
        out vec3 vFieldColor;
        void main()
        {
            float s = uDeformScale;
            // A mesh with no displacement buffer reads zero for all four attributes, so
            // the position is aPos exactly (x + s*0 is x for every finite s) and the
            // normal expression is exactly zero, which takes the aNormal branch. That is
            // what keeps a part with no results rendering byte-identically.
            vec4 world = uModel * vec4(aPos + s * aDeformOffset, 1.0);
            vec3 nd = aDeformNormal0 + s * aDeformNormal1 + (s * s) * aDeformNormal2;
            vWorldPos = world.xyz;
            vNormal = mat3(uModel) * (dot(nd, nd) > 0.0 ? nd : aNormal);
            vOcclusion = aOcclusion;
            vFieldColor = aFieldColor;
            gl_Position = uProj * uView * world;
        }
        """;

    /// <summary>
    /// Lit mesh fragment shader: directional light + specular, selection highlight
    /// (uHighlight), section planes (the shared <see cref="SectionClip"/> rule; cut
    /// interiors via gl_FrontFacing), translucency (uAlpha), baked ambient
    /// occlusion (uAmbientOcclusion scales the per-vertex vOcclusion in; 0 = off and
    /// reproduces the pre-AO look exactly, since the factor is then exactly 1.0), and
    /// field colouring (uFieldColor mixes the per-vertex vFieldColor in over the part
    /// colour; 0 = off and leaves uColor bit-identical, since mix(x, y, 0.0) is
    /// x*1.0 + y*0.0 = x for any finite y — the same constant-when-absent rule the
    /// occlusion attribute follows, and what keeps a part with no results rendering
    /// byte-identically).
    /// </summary>
    public const string MeshFragment = SectionClip + """
        in vec3 vNormal;
        in vec3 vWorldPos;
        in float vOcclusion;
        in vec3 vFieldColor;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform vec3 uEyePos;
        uniform mat4 uView;
        uniform float uHighlight;
        uniform float uAlpha;
        uniform float uAmbientOcclusion;
        uniform float uFieldColor;
        uniform int uMatcap;
        out vec4 fragColor;
        // Analytic matcap: the lit-sphere image a matcap samples is PROCEDURAL here -
        // Gaussian lobes evaluated at the view-space normal - so no texture machinery
        // has to reach three front ends. The lobe constants ARE the material and live
        // in this one file, where window, offscreen and web share every shading
        // decision. uMatcap 0 is the standard lighting below, and a linked program's
        // uniforms initialize to 0, so a front end that says nothing gets exactly the
        // incumbent look. uView is the same program uniform the vertex shader reads;
        // a matcap is defined in the CAMERA's frame, which is what makes the material
        // stick to the view while the model turns under it.
        vec3 matcapShade(vec3 surface, vec3 n)
        {
            vec3 nv = normalize(mat3(uView) * n);
            vec2 p = nv.xy;
            if (uMatcap == 1)
            {
                // Clay: a broad key lobe upper-left, a soft fill opposite - the
                // classic studio sphere, matte.
                vec2 dk = p - vec2(-0.45, 0.55);
                vec2 df = p - vec2(0.55, -0.35);
                float key = exp(-2.2 * dot(dk, dk));
                float fill = 0.30 * exp(-2.0 * dot(df, df));
                return surface * min(0.34 + 0.62 * key + fill, 1.1);
            }
            // Metal: dark base, a tight near-white key highlight, a soft secondary
            // lobe and a strong silhouette rim - the polished-metal sphere.
            vec2 d1 = p - vec2(-0.50, 0.62);
            vec2 d2 = p - vec2(0.55, -0.20);
            float key = exp(-16.0 * dot(d1, d1));
            float soft = exp(-5.0 * dot(d2, d2));
            float rim = pow(1.0 - abs(nv.z), 3.0);
            return surface * (0.14 + 0.50 * soft + 0.60 * rim)
                + vec3(0.90, 0.92, 0.95) * (0.85 * key);
        }
        void main()
        {
            // The surface colour before lighting: the part's own, or the colour-mapped
            // field value where one is displayed. uFieldColor 0 leaves this exactly
            // uColor, which is what makes a part with no results render unchanged.
            vec3 surface = mix(uColor, vFieldColor, uFieldColor);
            if (uSectionEnabled > 0.5)
            {
                if (sectionClipped(vWorldPos))
                    discard;
                if (!gl_FrontFacing)
                {
                    // Cut cue: interiors exposed by the section plane show their
                    // backfaces as a flat, darker warm tint (no lighting), the
                    // standard CAD hint that you are looking at cut material.
                    // Baked occlusion is not applied here: it was baked for the
                    // outward surface, and cut material is a flat fill by design.
                    // NOTE: shader comments must stay pure ASCII - ANGLE's GLES
                    // translator rejects the whole shader on non-ASCII bytes.
                    vec3 cut = mix(surface, vec3(0.78, 0.47, 0.25), 0.55) * 0.72;
                    fragColor = vec4(mix(cut, vec3(1.0, 0.85, 0.35), uHighlight * 0.4), uAlpha);
                    return;
                }
            }
            vec3 n = normalize(vNormal);
            float diffuse = max(dot(n, -uLightDir), 0.0);
            vec3 v = normalize(uEyePos - vWorldPos);
            vec3 h = normalize(v - uLightDir);
            float specular = pow(max(dot(n, h), 0.0), 48.0) * 0.35;
            vec3 base = mix(surface, vec3(1.0, 0.85, 0.35), uHighlight * 0.55);
            // Occlusion darkens ambient and diffuse but not the specular highlight
            // (a direct-light term); uAmbientOcclusion 0 leaves the factor exactly 1.
            float ao = mix(1.0, vOcclusion, uAmbientOcclusion);
            if (uMatcap != 0)
            {
                // AO multiplies the matcap sample exactly as it multiplies the
                // ambient+diffuse product below; highlight and field colour are
                // already folded into base/surface, and the cut-face material above
                // is untouched - a matcap is about how a FILL is lit, nothing else.
                fragColor = vec4(matcapShade(base, n) * ao, uAlpha);
                return;
            }
            vec3 c = base * (0.22 + 0.78 * diffuse) * ao + vec3(specular);
            fragColor = vec4(c, uAlpha);
        }
        """;

    /// <summary>Flat-color line vertex shader (grid, axes, feature edges, wireframe).
    /// Carries the field-colour attribute so a WIREFRAME part can draw its simulation
    /// result: under the same constant-when-absent rule the mesh program follows, a
    /// line upload with no colour buffer reads the disabled attribute's constant and a
    /// draw that says nothing leaves uFieldColor at 0, so mix returns uColor
    /// bit-identically for every incumbent consumer (grid, axes, annotations, legend,
    /// isolines, cube).</summary>
    public const string LineVertex = """
        in vec3 aPos;
        in vec3 aFieldColor;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        out vec3 vWorldPos;
        out vec3 vFieldColor;
        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorldPos = world.xyz;
            vFieldColor = aFieldColor;
            gl_Position = uProj * uView * world;
        }
        """;

    /// <summary>Flat-color line fragment shader; lines that belong to the model are
    /// clipped by the section planes consistently with the fills. uFieldColor mixes the
    /// per-vertex field colour in exactly as the mesh program does (0 = off = uColor,
    /// since mix(x, y, 0.0) is x for any finite y).</summary>
    public const string LineFragment = SectionClip + """
        in vec3 vWorldPos;
        in vec3 vFieldColor;
        uniform vec3 uColor;
        uniform float uFieldColor;
        out vec4 fragColor;
        void main()
        {
            if (sectionClipped(vWorldPos))
                discard;
            fragColor = vec4(mix(uColor, vFieldColor, uFieldColor), 1.0);
        }
        """;

    /// <summary>Point-sprite vertex shader for the points view style. gl_PointSize
    /// needs GL_PROGRAM_POINT_SIZE enabled on desktop GL (always on under GLES).
    /// <para>It carries the displacement attribute too, because the points view draws
    /// the MESH buffer: a points view of a deformed result must show the displaced
    /// vertices, which is what the CPU deformation path gave it by uploading displaced
    /// positions. (Wireframe does not: <c>WireframeEdges</c> reads the source half-edge
    /// mesh, so it has never followed a deformation.)</para></summary>
    public const string PointVertex = """
        in vec3 aPos;
        in vec3 aFieldColor;
        in vec3 aDeformOffset;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform float uPointSize;
        uniform float uDeformScale;
        out vec3 vWorldPos;
        out vec3 vFieldColor;
        void main()
        {
            vec4 world = uModel * vec4(aPos + uDeformScale * aDeformOffset, 1.0);
            vWorldPos = world.xyz;
            vFieldColor = aFieldColor;
            gl_Position = uProj * uView * world;
            gl_PointSize = uPointSize;
        }
        """;

    /// <summary>Point-sprite fragment shader: round dots (square sprites trimmed via
    /// gl_PointCoord), section-clipped consistently with the model.</summary>
    public const string PointFragment = SectionClip + """
        in vec3 vWorldPos;
        in vec3 vFieldColor;
        uniform vec3 uColor;
        uniform float uFieldColor;
        out vec4 fragColor;
        void main()
        {
            if (sectionClipped(vWorldPos))
                discard;
            vec2 d = gl_PointCoord - vec2(0.5);
            if (dot(d, d) > 0.25)
                discard;
            fragColor = vec4(mix(uColor, vFieldColor, uFieldColor), 1.0);
        }
        """;

    /// <summary>Vertexless fullscreen triangle via gl_VertexID.</summary>
    public const string BackgroundVertex = """
        out vec2 vUv;
        void main()
        {
            vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
            vec2 p = corners[gl_VertexID];
            vUv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <summary>Vertical background gradient, dark CAD theme.</summary>
    public const string BackgroundFragment = """
        in vec2 vUv;
        out vec4 fragColor;
        void main()
        {
            vec3 bottom = vec3(0.09, 0.10, 0.12);
            vec3 top = vec3(0.18, 0.20, 0.24);
            fragColor = vec4(mix(bottom, top, vUv.y), 1.0);
        }
        """;
}

/// <summary>Orbit camera pose, snapshotable for persistence across process restarts.
/// <para>
/// Here rather than in EngrCAD.Viewer because it is the argument every front end passes
/// to <see cref="CameraMath"/> — the browser client cannot reference the desktop
/// assembly, and a second copy of the pose type is the first step to a second copy of
/// the orbit maths. The namespace is unchanged, so existing call sites are untouched.
/// </para></summary>
public sealed record CameraState(double Yaw, double Pitch, double Distance, Vector3d Target);

/// <summary>
/// Camera and projection math shared by both render paths (rendering-only; kernel math
/// stays in EngrCAD.Core). Column-vector convention, Z up.
/// <para>
/// Public because every front end needs the same framing: a browser client that
/// recomputed its own orbit eye, frustum or matrix packing would drift from the desktop
/// viewer exactly as the offscreen pass once did. That extends past the matrices to the
/// <b>input bindings</b> — <see cref="DragOrbit"/>, <see cref="DragPan"/>,
/// <see cref="DragZoom"/>, <see cref="WheelZoom"/> hold the pixels-to-radians constants
/// a viewport feels through, and a second front end that re-typed them would feel
/// subtly different for no reason anyone could later reconstruct.
/// </para>
/// </summary>
public static class CameraMath
{
    /// <summary>
    /// How far the orbit camera may pitch: just short of straight up/down, which keeps
    /// the <see cref="LookAt"/> up-vector non-degenerate. <c>ViewCubeMath.PitchLimit</c>
    /// is this value (the cube's poses and the orbit clamp must agree, or a snap to Top
    /// would be undone by the very next clamp).
    /// </summary>
    public const double PitchLimit = Math.PI / 2 - 0.01;

    /// <summary>Eye position of the orbit camera.</summary>
    public static Vector3d Eye(double yaw, double pitch, double distance, in Vector3d target) =>
        target + new Vector3d(
            distance * Math.Cos(pitch) * Math.Cos(yaw),
            distance * Math.Cos(pitch) * Math.Sin(yaw),
            distance * Math.Sin(pitch));

    /// <summary>View matrix looking from <paramref name="eye"/> at <paramref name="target"/>.</summary>
    public static Matrix4d LookAt(in Vector3d eye, in Vector3d target, in Vector3d up)
    {
        var f = (target - eye).Normalized();
        var r = f.Cross(up).Normalized();
        var u = r.Cross(f);
        return new Matrix4d(
            r.X, r.Y, r.Z, -r.Dot(eye),
            u.X, u.Y, u.Z, -u.Dot(eye),
            -f.X, -f.Y, -f.Z, f.Dot(eye),
            0, 0, 0, 1);
    }

    /// <summary>Perspective projection matrix.</summary>
    public static Matrix4d Perspective(double fovY, double aspect, double near, double far)
    {
        double t = 1.0 / Math.Tan(fovY / 2);
        return new Matrix4d(
            t / aspect, 0, 0, 0,
            0, t, 0, 0,
            0, 0, (far + near) / (near - far), 2 * far * near / (near - far),
            0, 0, -1, 0);
    }

    /// <summary>Orthographic projection matrix sized by half its vertical extent.</summary>
    public static Matrix4d Orthographic(double halfHeight, double aspect, double near, double far) => new(
        1 / (halfHeight * aspect), 0, 0, 0,
        0, 1 / halfHeight, 0, 0,
        0, 0, -2 / (far - near), -(far + near) / (far - near),
        0, 0, 0, 1);

    /// <summary>
    /// Near/far planes scaled from the camera distance and scene size, so large scenes
    /// neither clip at a fixed far plane nor need a max-distance clamp (a hardcoded
    /// 0.1/200 frustum cropped scenes wider than ~100 units).
    /// </summary>
    public static (double Near, double Far) FrustumPlanes(double distance, in Aabb sceneBounds)
    {
        double sceneReach = distance + (sceneBounds.IsEmpty ? 10 : sceneBounds.Size.Length);
        double near = Math.Max(distance * 0.005, 0.01);
        double far = Math.Max(200.0, sceneReach * 2);
        return (near, far);
    }

    /// <summary>Auto-framing orbit distance for a scene of the given bounds.</summary>
    public static double FrameDistance(in Aabb bounds) => Math.Max(bounds.Size.Length * 1.25 + 1, 2);

    /// <summary>
    /// The viewer's first-visit framing — the default yaw/pitch every front end starts
    /// at (offscreen renders with a null camera, the window's initial pose, turntable
    /// bases), distance from the bounds. One function so "the default view" cannot mean
    /// different angles in different front ends.
    /// </summary>
    public static CameraState DefaultCamera(in Aabb bounds) => bounds.IsEmpty
        ? new CameraState(0.7, 0.45, 15.0, (0, 0, 0))
        : new CameraState(0.7, 0.45, FrameDistance(bounds), bounds.Center);

    /// <summary>
    /// Zoom-out limit: generous multiple of the scene size (the frustum scales with the
    /// distance, so the cap only stops the scene shrinking to nothing).
    /// </summary>
    public static double MaxOrbitDistance(in Aabb sceneBounds) =>
        Math.Max(120.0, sceneBounds.IsEmpty ? 0 : sceneBounds.Size.Length * 6);

    // ---- orbit camera transitions ----
    //
    // Pure state -> state functions so every front end moves the camera the same way.
    // The desktop viewport and the Blazor client both call these; nothing about how a
    // drag feels lives in a front end.

    /// <summary>The pose with pitch and distance forced into their legal ranges — the
    /// rule any externally supplied <see cref="CameraState"/> passes through.</summary>
    public static CameraState Clamped(CameraState camera, in Aabb sceneBounds) => camera with
    {
        Pitch = Math.Clamp(camera.Pitch, -PitchLimit, PitchLimit),
        Distance = Math.Clamp(camera.Distance, 0.5, MaxOrbitDistance(sceneBounds)),
    };

    /// <summary>Rotates about the target by the given radians (yaw free, pitch clamped).</summary>
    public static CameraState Orbit(CameraState camera, double yawDelta, double pitchDelta) => camera with
    {
        Yaw = camera.Yaw + yawDelta,
        Pitch = Math.Clamp(camera.Pitch + pitchDelta, -PitchLimit, PitchLimit),
    };

    /// <summary>Scales the orbit distance, clamped to the scene's zoom range.</summary>
    public static CameraState Zoom(CameraState camera, double factor, in Aabb sceneBounds) => camera with
    {
        Distance = Math.Clamp(camera.Distance * factor, 0.5, MaxOrbitDistance(sceneBounds)),
    };

    /// <summary>
    /// Slides the target in the screen plane by <paramref name="dx"/>/<paramref name="dy"/>
    /// pixels. Screen-right and screen-up are derived from the pose, and the world
    /// distance per pixel scales with the orbit distance so panning feels the same
    /// however far out you are.
    /// </summary>
    public static CameraState Pan(CameraState camera, double dx, double dy)
    {
        var eyeDir = new Vector3d(
            Math.Cos(camera.Pitch) * Math.Cos(camera.Yaw),
            Math.Cos(camera.Pitch) * Math.Sin(camera.Yaw),
            Math.Sin(camera.Pitch));
        var right = eyeDir.Cross(Vector3d.UnitZ).Normalized();
        var up = right.Cross(eyeDir);   // eyeDir points from target toward eye, so this is screen-up
        double scale = camera.Distance * 0.0018;
        return camera with { Target = camera.Target + right * (dx * scale) + up * (dy * scale) };
    }

    /// <summary>Plain drag: orbit. Dragging right turns the model right, so yaw takes
    /// the negated horizontal travel.</summary>
    public static CameraState DragOrbit(CameraState camera, double dx, double dy) =>
        Orbit(camera, -dx * 0.01, dy * 0.01);

    /// <summary>Shift+drag (or right/middle drag): pan.</summary>
    public static CameraState DragPan(CameraState camera, double dx, double dy) =>
        Pan(camera, dx, dy);

    /// <summary>Ctrl+drag: zoom, dragging down to zoom out.</summary>
    public static CameraState DragZoom(CameraState camera, double dy, in Aabb sceneBounds) =>
        Zoom(camera, Math.Pow(1.006, dy), sceneBounds);

    /// <summary>Wheel or trackpad scroll: zoom. Trackpads deliver many small fractional
    /// deltas, which the exponential handles smoothly.</summary>
    public static CameraState WheelZoom(CameraState camera, double delta, in Aabb sceneBounds) =>
        Zoom(camera, Math.Pow(0.88, delta), sceneBounds);

    /// <summary>Keyboard orbit/zoom step (arrow keys, +/-).</summary>
    public const double KeyStep = 0.07;

    /// <summary>Writes a Matrix4d as the column-major float[16] OpenGL expects.</summary>
    public static void WriteColumnMajor(in Matrix4d m, Span<float> dst)
    {
        dst[0] = (float)m.M11; dst[1] = (float)m.M21; dst[2] = (float)m.M31; dst[3] = (float)m.M41;
        dst[4] = (float)m.M12; dst[5] = (float)m.M22; dst[6] = (float)m.M32; dst[7] = (float)m.M42;
        dst[8] = (float)m.M13; dst[9] = (float)m.M23; dst[10] = (float)m.M33; dst[11] = (float)m.M43;
        dst[12] = (float)m.M14; dst[13] = (float)m.M24; dst[14] = (float)m.M34; dst[15] = (float)m.M44;
    }
}

/// <summary>
/// Scene-furniture geometry shared by both render paths.
/// <para>
/// Public because a front end builds the same grid, axes and line buffers before
/// handing them to its own GL: these produce the raw float arrays, and the upload
/// helpers that consume them (<c>RenderUploads</c>, EngrCAD.Viewer) stay behind because
/// they take a <c>GL</c>.
/// </para>
/// </summary>
public static class RenderGeometry
{
    /// <summary>Adaptive 1-2-5 ground grid on z = 0 sized to the scene, plus RGB world
    /// axes (X then Y then Z, two vertices each, at the end of the axes array).</summary>
    public static (float[] Grid, float[] Axes) BuildGridAndAxes(in Aabb bounds)
    {
        double diagonal = bounds.IsEmpty ? 10 : bounds.Size.Length;
        double spacing = NiceStep(diagonal / 10);
        float extent = (float)(spacing * 12);

        var lines = new List<float>();
        for (int i = -12; i <= 12; i++)
        {
            float p = (float)(i * spacing);
            lines.AddRange([p, -extent, 0, p, extent, 0]);   // parallel to Y
            lines.AddRange([-extent, p, 0, extent, p, 0]);   // parallel to X
        }

        float a = extent * 0.6f;
        float[] axes =
        [
            0, 0, 0, a, 0, 0,   // +X
            0, 0, 0, 0, a, 0,   // +Y
            0, 0, 0, 0, 0, a,   // +Z
        ];
        return ([.. lines], axes);
    }

    /// <summary>Rounds a raw step to the nearest 1/2/5 decade value.</summary>
    public static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
        double residual = raw / magnitude;
        return magnitude * (residual < 1.5 ? 1 : residual < 3.5 ? 2 : residual < 7.5 ? 5 : 10);
    }

    /// <summary>Flattens line segments into the xyz-per-vertex array the line program draws.</summary>
    public static float[] SegmentVertices(IReadOnlyList<(Vector3d A, Vector3d B)> segments)
    {
        var vertices = new float[segments.Count * 6];
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            vertices[i * 6 + 0] = (float)a.X;
            vertices[i * 6 + 1] = (float)a.Y;
            vertices[i * 6 + 2] = (float)a.Z;
            vertices[i * 6 + 3] = (float)b.X;
            vertices[i * 6 + 4] = (float)b.Y;
            vertices[i * 6 + 5] = (float)b.Z;
        }
        return vertices;
    }
}
