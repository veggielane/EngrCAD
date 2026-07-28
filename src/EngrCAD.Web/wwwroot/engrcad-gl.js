// The WebGL2 half of the EngrCAD web viewer.
//
// DESIGN RULE, and the whole reason this file is as small as it is: **no GLSL lives
// here.** Shader source is handed in from C# (EngrCAD.Viewer.Core's ViewerShaders), the
// same strings the desktop window and the offscreen renderer compile. RenderCore.cs
// exists because the window and offscreen passes once duplicated ~150 lines and drifted
// silently; a third front end copying the shaders into JavaScript would be that mistake
// a third time, in a language where nothing would catch it.
//
// This module therefore only does what a browser must: own the GL context, upload the
// buffers C# hands it, and issue draws. Every decision about what the scene LOOKS like
// -- shading, section clipping, camera framing, draw order -- stays in .NET.
//
// Buffers arrive as Uint8Array (Blazor transfers byte[] as binary, not JSON) and are
// reinterpreted here as Float32Array/Uint32Array. Packing on the C# side keeps one
// copy rather than marshalling arrays element by element.

const contexts = new Map();
let nextId = 1;

/** Creates the GL context for a canvas. Returns a handle, or throws if WebGL2 is absent. */
export function createContext(canvas) {
    const gl = canvas.getContext('webgl2', {
        antialias: true,
        depth: true,
        alpha: false,
        preserveDrawingBuffer: false,
    });
    if (!gl) {
        throw new Error('WebGL2 is not available in this browser.');
    }
    // 32-bit indices are core in WebGL2, but float render targets and derivatives are
    // not -- nothing here needs them, which is why the desktop shaders port unchanged.
    const id = nextId++;
    contexts.set(id, {
        gl,
        canvas,
        programs: new Map(),
        meshes: new Map(),
        lines: new Map(),
        // An attributeless VAO for draws that generate their own vertices from
        // gl_VertexID (the background gradient), and the last frame drawn, which is what
        // capture() re-runs. Neither is a decision about what a frame looks like.
        emptyVao: gl.createVertexArray(),
        lastFrame: null,
    });
    gl.enable(gl.DEPTH_TEST);
    gl.enable(gl.CULL_FACE);
    gl.cullFace(gl.BACK);
    return id;
}

export function disposeContext(id) {
    const ctx = contexts.get(id);
    if (!ctx) return;
    for (const mesh of ctx.meshes.values()) releaseMesh(ctx.gl, mesh);
    for (const line of ctx.lines.values()) releaseLines(ctx.gl, line);
    for (const program of ctx.programs.values()) ctx.gl.deleteProgram(program.handle);
    ctx.gl.deleteVertexArray(ctx.emptyVao);
    contexts.delete(id);
}

/**
 * Routes subsequent pointer events for `pointerId` to the canvas until the button is
 * released, so a drag that leaves the element keeps orbiting -- the browser's answer to
 * the desktop viewer's window-level handlers. Failures are ignored: a pointer that has
 * already been released cannot be captured, and that is not an error worth surfacing.
 */
export function capturePointer(canvas, pointerId) {
    try { canvas.setPointerCapture(pointerId); } catch { /* pointer already gone */ }
}

function require(id) {
    const ctx = contexts.get(id);
    if (!ctx) throw new Error(`No GL context ${id}; was createContext called?`);
    return ctx;
}

// ---- programs ---------------------------------------------------------------------

/**
 * Compiles and links a named program from C#-supplied source. Attribute locations are
 * bound explicitly to the same slots the desktop path uses (0 position, 1 normal,
 * 2 occlusion) so one vertex layout serves every front end.
 */
export function createProgram(id, name, vertexSource, fragmentSource, bindAttributes) {
    const ctx = require(id);
    const gl = ctx.gl;
    const vs = compile(gl, gl.VERTEX_SHADER, vertexSource, name);
    const fs = compile(gl, gl.FRAGMENT_SHADER, fragmentSource, name);
    const handle = gl.createProgram();
    gl.attachShader(handle, vs);
    gl.attachShader(handle, fs);
    if (bindAttributes) {
        gl.bindAttribLocation(handle, 0, 'aPos');
        gl.bindAttribLocation(handle, 1, 'aNormal');
        gl.bindAttribLocation(handle, 2, 'aOcclusion');
    }
    gl.linkProgram(handle);
    if (!gl.getProgramParameter(handle, gl.LINK_STATUS)) {
        const log = gl.getProgramInfoLog(handle);
        gl.deleteProgram(handle);
        throw new Error(`Shader link failed (${name}): ${log}`);
    }
    gl.detachShader(handle, vs);
    gl.detachShader(handle, fs);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    ctx.programs.set(name, { handle, uniforms: new Map() });
}

function compile(gl, type, source, name) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        const log = gl.getShaderInfoLog(shader);
        gl.deleteShader(shader);
        const stage = type === gl.VERTEX_SHADER ? 'vertex' : 'fragment';
        // Reported with the stage and program name because a link failure downstream
        // is otherwise indistinguishable from a missing program.
        throw new Error(`Shader compile failed (${name} ${stage}): ${log}`);
    }
    return shader;
}

/** Uniform locations are looked up once and memoized -- getUniformLocation is a
 *  string-keyed lookup into the driver on every call otherwise. */
function uniform(ctx, program, name) {
    let location = program.uniforms.get(name);
    if (location === undefined) {
        location = ctx.gl.getUniformLocation(program.handle, name);
        program.uniforms.set(name, location);
    }
    return location;
}

// ---- geometry ---------------------------------------------------------------------

/**
 * Uploads (or replaces) a mesh. `positions`/`normals` are packed float32 triples and
 * `indices` packed uint32, all as raw bytes. Occlusion is optional: a VAO with no
 * occlusion buffer reads the context constant 1.0, which is exactly the AO-off shading
 * -- the same property the desktop viewer relies on to stream bakes in.
 */
export function uploadMesh(id, key, positions, normals, indices, occlusion) {
    const ctx = require(id);
    const gl = ctx.gl;
    const existing = ctx.meshes.get(key);
    if (existing) releaseMesh(gl, existing);

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);

    const posBuffer = buffer(gl, gl.ARRAY_BUFFER, positions);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);

    const normalBuffer = buffer(gl, gl.ARRAY_BUFFER, normals);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);

    let occlusionBuffer = null;
    if (occlusion && occlusion.byteLength > 0) {
        occlusionBuffer = buffer(gl, gl.ARRAY_BUFFER, occlusion);
        gl.enableVertexAttribArray(2);
        gl.vertexAttribPointer(2, 1, gl.FLOAT, false, 0, 0);
    } else {
        gl.disableVertexAttribArray(2);
        gl.vertexAttrib1f(2, 1.0);
    }

    const indexBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);

    gl.bindVertexArray(null);
    ctx.meshes.set(key, {
        vao, posBuffer, normalBuffer, occlusionBuffer, indexBuffer,
        indexCount: indices.byteLength / 4,
        vertexCount: positions.byteLength / 12,
    });
}

/** Uploads a line list (feature edges, grid, axes): packed float32 position triples. */
export function uploadLines(id, key, positions) {
    const ctx = require(id);
    const gl = ctx.gl;
    const existing = ctx.lines.get(key);
    if (existing) releaseLines(gl, existing);

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const posBuffer = buffer(gl, gl.ARRAY_BUFFER, positions);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
    gl.bindVertexArray(null);

    ctx.lines.set(key, { vao, posBuffer, vertexCount: positions.byteLength / 12 });
}

function buffer(gl, target, bytes) {
    const handle = gl.createBuffer();
    gl.bindBuffer(target, handle);
    gl.bufferData(target, bytes, gl.STATIC_DRAW);
    return handle;
}

function releaseMesh(gl, mesh) {
    gl.deleteVertexArray(mesh.vao);
    gl.deleteBuffer(mesh.posBuffer);
    gl.deleteBuffer(mesh.normalBuffer);
    gl.deleteBuffer(mesh.indexBuffer);
    if (mesh.occlusionBuffer) gl.deleteBuffer(mesh.occlusionBuffer);
}

function releaseLines(gl, line) {
    gl.deleteVertexArray(line.vao);
    gl.deleteBuffer(line.posBuffer);
}

export function releaseGeometry(id, key) {
    const ctx = require(id);
    const mesh = ctx.meshes.get(key);
    if (mesh) { releaseMesh(ctx.gl, mesh); ctx.meshes.delete(key); }
    const line = ctx.lines.get(key);
    if (line) { releaseLines(ctx.gl, line); ctx.lines.delete(key); }
}

// ---- drawing ----------------------------------------------------------------------

/**
 * Renders one frame. `frame` is the JSON description C# builds -- clear colour, the
 * view-projection matrix, and a draw list. Everything in it was decided in .NET: which
 * program, which uniforms, which order. This function contains no policy.
 */
export function render(id, frame) {
    const ctx = require(id);
    ctx.lastFrame = frame;
    drawFrame(ctx, frame);
}

/**
 * Re-draws the last frame and reads the pixels back as RGBA rows, BOTTOM row first (raw
 * glReadPixels order -- flipping is the caller's business, like every other decision).
 *
 * The re-draw is not redundant: the context is created without preserveDrawingBuffer, so
 * the buffer C# last asked for is gone by the time a separate interop call arrives. This
 * is the browser's equivalent of the desktop viewer's SaveScreenshot, and it is what lets
 * a headless run PROVE that pixels were drawn rather than infer it from a quiet log.
 */
export function capture(id) {
    const ctx = require(id);
    if (!ctx.lastFrame) throw new Error('Nothing has been rendered yet.');
    drawFrame(ctx, ctx.lastFrame);
    const gl = ctx.gl;
    const width = gl.drawingBufferWidth;
    const height = gl.drawingBufferHeight;
    const pixels = new Uint8Array(width * height * 4);
    gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
    return pixels;
}

/** The drawing buffer's size in device pixels (capture() returns rows of this width). */
export function drawingBufferSize(id) {
    const gl = require(id).gl;
    return [gl.drawingBufferWidth, gl.drawingBufferHeight];
}

function drawFrame(ctx, frame) {
    const gl = ctx.gl;

    resize(ctx);
    const fullViewport = [0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight];
    const [r, g, b] = frame.clear;
    gl.clearColor(r, g, b, 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

    for (const call of frame.draws) {
        const program = ctx.programs.get(call.program);
        if (!program) throw new Error(`No program '${call.program}'`);
        gl.useProgram(program.handle);

        // Frame-constant uniforms first, then the call's own -- the call wins on a clash.
        for (const [name, value] of Object.entries(frame.shared ?? {})) {
            setUniform(ctx, program, name, value);
        }
        for (const [name, value] of Object.entries(call.uniforms ?? {})) {
            setUniform(ctx, program, name, value);
        }

        gl.depthMask(call.depthWrite !== false);
        if (call.depthTest === false) gl.disable(gl.DEPTH_TEST); else gl.enable(gl.DEPTH_TEST);
        if (call.blend) {
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        } else {
            gl.disable(gl.BLEND);
        }
        if (call.cull === false) gl.disable(gl.CULL_FACE); else gl.enable(gl.CULL_FACE);
        if (call.polygonOffset) {
            gl.enable(gl.POLYGON_OFFSET_FILL);
            gl.polygonOffset(call.polygonOffset[0], call.polygonOffset[1]);
        } else {
            gl.disable(gl.POLYGON_OFFSET_FILL);
        }

        // Depth clear + per-draw viewport, both decided in C# (the view cube's overlay
        // trick). glClear ignores the viewport rect, so the order matches the desktop:
        // clear first, then narrow the viewport for the draw.
        if (call.clearDepth) gl.clear(gl.DEPTH_BUFFER_BIT);
        const vp = call.viewport ?? fullViewport;
        gl.viewport(vp[0], vp[1], vp[2], vp[3]);

        // No geometry key: the shader generates its own vertices from gl_VertexID (the
        // background gradient's fullscreen triangle). The count comes from C# like
        // everything else.
        if (!call.geometry) {
            gl.bindVertexArray(ctx.emptyVao);
            gl.drawArrays(gl.TRIANGLES, 0, call.count ?? 3);
            continue;
        }

        const mesh = ctx.meshes.get(call.geometry);
        if (mesh) {
            gl.bindVertexArray(mesh.vao);
            if (call.mode === 'points') {
                gl.drawArrays(gl.POINTS, 0, mesh.vertexCount);
            } else {
                gl.drawElements(gl.TRIANGLES, mesh.indexCount, gl.UNSIGNED_INT, 0);
            }
            continue;
        }
        const line = ctx.lines.get(call.geometry);
        if (line) {
            // first/count let one buffer serve several draws: the three world axes are
            // consecutive vertex pairs in a single upload, coloured separately. Mode
            // 'triangles' draws position-only fills through the same layout (the view
            // cube's faces, drawn per face for their colour - the desktop does the
            // same through its line program).
            gl.bindVertexArray(line.vao);
            const mode = call.mode === 'triangles' ? gl.TRIANGLES : gl.LINES;
            gl.drawArrays(mode, call.first ?? 0, call.count ?? line.vertexCount);
            continue;
        }
        throw new Error(`No geometry '${call.geometry}'`);
    }
    gl.bindVertexArray(null);
}

function setUniform(ctx, program, name, value) {
    const gl = ctx.gl;
    const location = uniform(ctx, program, name);
    if (location === null) return;   // optimized out of this program; not an error
    if (typeof value === 'number') { gl.uniform1f(location, value); return; }
    if (typeof value === 'boolean') { gl.uniform1i(location, value ? 1 : 0); return; }
    if (!Array.isArray(value)) {
        // Typed markers (C#'s IntUniform / Vec4ArrayUniform). A plain JSON number
        // cannot say whether the uniform is a float or an int -- uniform1f on an int
        // uniform is a GL error -- and a 16-float vec4[4] array is indistinguishable
        // from a mat4. WHICH uniforms need which type is decided in C#; this only
        // dispatches on the marker's shape.
        if (typeof value.int === 'number') { gl.uniform1i(location, value.int); return; }
        if (Array.isArray(value.vec4)) { gl.uniform4fv(location, value.vec4); return; }
        return;   // unknown marker: leave the uniform at its default, as with a null location
    }
    switch (value.length) {
        case 2: gl.uniform2fv(location, value); break;
        case 3: gl.uniform3fv(location, value); break;
        case 4: gl.uniform4fv(location, value); break;
        case 16: gl.uniformMatrix4fv(location, false, value); break;
        default: gl.uniform1fv(location, value); break;
    }
}

/** Matches the drawing buffer to the canvas's CSS size times the device pixel ratio,
 *  so the render is sharp on high-DPI displays and the aspect ratio is honest. */
function resize(ctx) {
    const { gl, canvas } = ctx;
    const dpr = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }
    gl.viewport(0, 0, canvas.width, canvas.height);
}

/**
 * The canvas's CSS size in device-independent pixels, plus the device-pixel ratio the
 * drawing buffer is sized at -- C# needs the size for the camera's aspect ratio and for
 * pick-ray unprojection, and the ratio to size point sprites, which are measured in
 * framebuffer pixels. All three are facts only the browser has; what is DONE with them
 * stays in .NET.
 */
export function viewportSize(id) {
    const ctx = require(id);
    return [ctx.canvas.clientWidth, ctx.canvas.clientHeight, window.devicePixelRatio || 1];
}
