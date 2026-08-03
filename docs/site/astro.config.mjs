// @ts-check
import { fileURLToPath } from 'node:url';
import { defineConfig, passthroughImageService } from 'astro/config';
import starlight from '@astrojs/starlight';
import { rewriteDocLinks } from './src/rewrite-doc-links.mjs';

// GitHub Pages serves a project site from https://<user>.github.io/<repo>/, so the site
// needs a base path -- but a custom domain serves it from the root, and a local build
// from nothing at all. `actions/configure-pages` reports whichever applies, and CI passes
// it in here, so the repository name is never baked into the source (the same rule the
// WebAssembly demo already follows; see docs/examples/web.md).
const base = process.env.DOCS_BASE || '/';
const site = process.env.DOCS_SITE || undefined;

// The markdown lives in `docs/`, one level up; see src/content.config.ts for why.
const contentRoot = fileURLToPath(new URL('../', import.meta.url));

export default defineConfig({
  site,
  base,
  // Trailing slashes are load-bearing here: the pages that embed the live WebAssembly
  // demo reach it with a RELATIVE link, which only resolves correctly when the page's own
  // URL ends in a slash. Emitting them everywhere means the relative form works from a
  // site root, from /EngrCAD/, and from a local preview alike.
  trailingSlash: 'always',
  // No image transformation. The example screenshots are generated and byte-committed by
  // EngrCAD.DocsGen, and several of them are APNG animations -- re-encoding a PNG through
  // sharp would silently drop every frame after the first. Passthrough still resolves and
  // fingerprints the assets; it just does not touch the bytes.
  image: { service: passthroughImageService() },
  markdown: { rehypePlugins: [rewriteDocLinks({ contentRoot, base })] },
  vite: {
    // The content collection's base is `../`, so the dev server has to be allowed to read
    // the images that sit beside the markdown.
    server: { fs: { allow: ['..'] } },
  },
  integrations: [
    starlight({
      title: 'EngrCAD',
      description:
        'A hybrid B-Rep / implicit / mesh geometry kernel for modern .NET, with executable documentation.',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/veggielane/EngrCAD' },
      ],
      sidebar: [
        { label: 'Getting started', slug: 'getting-started' },
        {
          label: 'Examples',
          items: [
            { label: 'Primitives', slug: 'examples/primitives' },
            { label: 'Sketching', slug: 'examples/sketching' },
            { label: '2D sketch booleans', slug: 'examples/sketch-booleans' },
            { label: '2D views (offset, section, silhouette)', slug: 'examples/2d-views' },
            { label: 'Space-filling curves & 2D infill', slug: 'examples/infill' },
            { label: 'DXF & SVG (2D interchange)', slug: 'examples/dxf-svg' },
            { label: 'Drawings (hidden lines, sheets, dimensions)', slug: 'examples/drawings' },
            { label: 'Extrude, revolve, sweep', slug: 'examples/extrude-revolve-sweep' },
            { label: 'Booleans', slug: 'examples/booleans' },
            { label: 'Blends, offset, shell, lattice', slug: 'examples/implicit' },
            { label: 'Transforms & patterns', slug: 'examples/transforms-patterns' },
            { label: 'Holes & standard sizes', slug: 'examples/holes' },
            { label: 'Text', slug: 'examples/text' },
            { label: 'Heightmap terrain', slug: 'examples/heightmaps' },
            { label: 'Threads', slug: 'examples/threads' },
            { label: 'Chamfer & fillet', slug: 'examples/chamfer-fillet' },
            { label: 'Loft, draft & shell', slug: 'examples/loft-draft-shell' },
            { label: 'Sheet metal', slug: 'examples/sheet-metal' },
            { label: 'Frames & weldments', slug: 'examples/frames' },
            { label: 'Parametric features', slug: 'examples/features' },
            { label: 'Geometry inputs for features', slug: 'examples/geometry-inputs' },
            { label: 'Design studies', slug: 'examples/design-studies' },
            { label: 'Selecting faces & edges', slug: 'examples/selection' },
            { label: '3D annotations (PMI)', slug: 'examples/annotations' },
            { label: 'Results & fields', slug: 'examples/fields' },
            { label: 'Manufacturability checks', slug: 'examples/manufacturability' },
            { label: 'Remeshing', slug: 'examples/remeshing' },
            { label: 'Tessellation quality', slug: 'examples/quality' },
            { label: 'Tetrahedral meshing (FEA)', slug: 'examples/fea-meshing' },
            { label: 'Structural analysis (FEA)', slug: 'examples/fea-structural' },
            { label: 'Thermal analysis (FEA)', slug: 'examples/fea-thermal' },
            { label: 'Modal analysis (FEA)', slug: 'examples/fea-modal' },
            { label: 'Buckling and frequency response (FEA)', slug: 'examples/fea-buckling' },
            { label: 'Transient dynamics (FEA)', slug: 'examples/fea-transient' },
            { label: 'Fatigue (FEA)', slug: 'examples/fea-fatigue' },
            { label: 'Three representations', slug: 'examples/representations' },
            { label: 'Saving documents', slug: 'examples/documents' },
            { label: 'Exports', slug: 'examples/exports' },
            { label: 'Importing meshes', slug: 'examples/import' },
            { label: 'Packing a build plate', slug: 'examples/packing' },
            { label: 'Assemblies', slug: 'examples/assemblies' },
            { label: 'Materials & mass', slug: 'examples/materials' },
            { label: 'Standard components', slug: 'examples/components' },
            { label: 'Mechanisms', slug: 'examples/mechanisms' },
            { label: 'Gears', slug: 'examples/gears' },
            { label: 'Animation', slug: 'examples/animation' },
            { label: 'Viewer', slug: 'examples/viewer' },
            { label: 'Scripting (.csx)', slug: 'examples/scripting' },
            { label: 'In the browser (WebAssembly)', slug: 'examples/web' },
            { label: 'AI assistants (MCP)', slug: 'examples/mcp' },
            { label: 'LINQ spatial queries', slug: 'examples/queries' },
          ],
        },
        { label: 'Writing examples', slug: 'writing-examples' },
        { label: 'API reference', link: 'api/' },
      ],
    }),
  ],
});
