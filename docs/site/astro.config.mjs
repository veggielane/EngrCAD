// @ts-check
import { fileURLToPath } from 'node:url';
import { defineConfig, passthroughImageService } from 'astro/config';
import starlight from '@astrojs/starlight';
import { rewriteDocLinks } from './src/rewrite-doc-links.mjs';
import { liveExamples, readLiveManifest } from './src/live-examples.mjs';

// GitHub Pages serves a project site from https://<user>.github.io/<repo>/, so the site
// needs a base path -- but a custom domain serves it from the root, and a local build
// from nothing at all. `actions/configure-pages` reports whichever applies, and CI passes
// it in here, so the repository name is never baked into the source (the same rule the
// WebAssembly demo already follows; see docs/examples/web.md).
const base = process.env.DOCS_BASE || '/';
const site = process.env.DOCS_SITE || undefined;

// The markdown lives in `docs/`, one level up; see src/content.config.ts for why.
const contentRoot = fileURLToPath(new URL('../', import.meta.url));

// Which example screenshots also get a "Run it" button, from the manifest EngrCAD.DocsGen
// writes. Absent (a checkout that has never run the generator) means no buttons and an
// otherwise identical site.
const { live } = readLiveManifest(contentRoot);

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
  markdown: {
    rehypePlugins: [
      rewriteDocLinks({ contentRoot, base }),
      liveExamples({ contentRoot, live }),
    ],
  },
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
      // The live-example poster. Both halves are deliberately tiny and inline: the whole
      // feature is one click handler that swaps an <img> for an <iframe>, and the iframe's
      // URL comes from the element's own data attribute (see src/live-examples.mjs) so the
      // repository name is never written into the source and the link checker never sees a
      // reference to a directory CI merges in later.
      head: [
        {
          tag: 'style',
          content: `
.engrcad-live { position: relative; }
.engrcad-live > img, .engrcad-live > iframe { display: block; width: 100%; border-radius: .3rem; }
.engrcad-live > iframe { border: 0; aspect-ratio: 10 / 7; background: #1c1e22; }
.engrcad-live-run {
  position: absolute; right: .5rem; bottom: .5rem;
  font: inherit; font-size: .8rem; line-height: 1.2;
  padding: .35rem .7rem; border-radius: 999px; cursor: pointer;
  border: 1px solid var(--sl-color-gray-5); color: var(--sl-color-white);
  background: color-mix(in srgb, var(--sl-color-black) 78%, transparent);
}
.engrcad-live-run:hover { border-color: var(--sl-color-accent); }
@media (prefers-reduced-motion: no-preference) { .engrcad-live-run { transition: border-color .15s; } }
`,
        },
        {
          tag: 'script',
          content: `
document.addEventListener('click', function (event) {
  var button = event.target.closest && event.target.closest('.engrcad-live-run');
  if (!button) return;
  var box = button.parentElement;
  var image = box.querySelector('img');
  var frame = box.querySelector('iframe');
  if (frame) { frame.remove(); image.hidden = false; button.textContent = 'Run it in your browser'; return; }
  frame = document.createElement('iframe');
  frame.src = box.dataset.src;
  frame.title = 'Live example: ' + box.dataset.example;
  if (image.naturalWidth && image.naturalHeight) {
    frame.style.aspectRatio = image.naturalWidth + ' / ' + image.naturalHeight;
  }
  image.hidden = true;
  box.insertBefore(frame, button);
  button.textContent = 'Show the screenshot';
});
`,
        },
      ],
      sidebar: [
        { label: 'Getting started', slug: 'getting-started' },
        // Grouped by what a reader is trying to DO, and collapsed by default: one flat
        // list of 52 example pages is a wall rather than a table of contents. Starlight
        // auto-expands whichever group holds the current page, so a reader still sees
        // their neighbours. Every page must appear exactly once — `check-links.mjs`
        // reports any page that is built but unreachable from here, so a page dropped
        // during a regroup fails the build instead of going quietly missing.
        {
          label: 'Sketching & 2D',
          collapsed: true,
          items: [
            { label: 'Sketching', slug: 'examples/sketching' },
            { label: '2D sketch booleans', slug: 'examples/sketch-booleans' },
            { label: '2D views (offset, section, silhouette)', slug: 'examples/2d-views' },
            { label: 'Space-filling curves & 2D infill', slug: 'examples/infill' },
            { label: 'DXF & SVG (2D interchange)', slug: 'examples/dxf-svg' },
          ],
        },
        {
          label: 'Modelling',
          collapsed: true,
          items: [
            { label: 'Primitives', slug: 'examples/primitives' },
            { label: 'Extrude, revolve, sweep', slug: 'examples/extrude-revolve-sweep' },
            { label: 'Booleans', slug: 'examples/booleans' },
            { label: 'Blends, offset, shell, lattice', slug: 'examples/implicit' },
            { label: 'Loft, draft & shell', slug: 'examples/loft-draft-shell' },
            { label: 'Transforms & patterns', slug: 'examples/transforms-patterns' },
            { label: 'Text', slug: 'examples/text' },
            { label: 'Heightmap terrain', slug: 'examples/heightmaps' },
            { label: 'Three representations', slug: 'examples/representations' },
          ],
        },
        {
          label: 'Features & detailing',
          collapsed: true,
          items: [
            { label: 'Holes & standard sizes', slug: 'examples/holes' },
            { label: 'Threads', slug: 'examples/threads' },
            { label: 'Chamfer & fillet', slug: 'examples/chamfer-fillet' },
            { label: 'Sheet metal', slug: 'examples/sheet-metal' },
            { label: 'Frames & weldments', slug: 'examples/frames' },
            { label: 'Parametric features', slug: 'examples/features' },
            { label: 'Configurations (family tables)', slug: 'examples/configurations' },
            { label: 'Geometry inputs for features', slug: 'examples/geometry-inputs' },
            { label: 'Selecting faces & edges', slug: 'examples/selection' },
          ],
        },
        {
          label: 'Assemblies & mechanisms',
          collapsed: true,
          items: [
            { label: 'Assemblies', slug: 'examples/assemblies' },
            { label: 'Standard components', slug: 'examples/components' },
            { label: 'Materials & mass', slug: 'examples/materials' },
            { label: 'Mechanisms', slug: 'examples/mechanisms' },
            { label: 'Gears', slug: 'examples/gears' },
          ],
        },
        {
          label: 'Simulation',
          collapsed: true,
          items: [
            { label: 'Tetrahedral meshing', slug: 'examples/fea-meshing' },
            { label: 'Structural analysis', slug: 'examples/fea-structural' },
            { label: 'Thermal analysis', slug: 'examples/fea-thermal' },
            { label: 'Modal analysis', slug: 'examples/fea-modal' },
            { label: 'Buckling & frequency response', slug: 'examples/fea-buckling' },
            { label: 'Transient dynamics', slug: 'examples/fea-transient' },
            { label: 'Fatigue', slug: 'examples/fea-fatigue' },
            { label: 'Topology optimisation', slug: 'examples/fea-topology' },
            { label: 'Results & fields', slug: 'examples/fields' },
          ],
        },
        {
          label: 'Manufacturing & drawings',
          collapsed: true,
          items: [
            { label: 'Manufacturability checks', slug: 'examples/manufacturability' },
            { label: 'Anti-drill tamper mesh', slug: 'examples/tamper-mesh' },
            { label: 'Packing a build plate', slug: 'examples/packing' },
            { label: 'Drawings (hidden lines, sheets, dimensions)', slug: 'examples/drawings' },
            { label: '3D annotations (PMI)', slug: 'examples/annotations' },
            { label: 'Exports', slug: 'examples/exports' },
          ],
        },
        {
          label: 'Meshing & import',
          collapsed: true,
          items: [
            { label: 'Remeshing', slug: 'examples/remeshing' },
            { label: 'Tessellation quality', slug: 'examples/quality' },
            { label: 'Importing meshes', slug: 'examples/import' },
          ],
        },
        {
          label: 'Documents & viewing',
          collapsed: true,
          items: [
            { label: 'Saving documents', slug: 'examples/documents' },
            { label: 'Viewer', slug: 'examples/viewer' },
            { label: 'Animation', slug: 'examples/animation' },
          ],
        },
        {
          label: 'Automation & integration',
          collapsed: true,
          items: [
            { label: 'Design studies', slug: 'examples/design-studies' },
            { label: 'Scripting (.csx)', slug: 'examples/scripting' },
            { label: 'In the browser (WebAssembly)', slug: 'examples/web' },
            { label: 'AI assistants (MCP)', slug: 'examples/mcp' },
            { label: 'LINQ spatial queries', slug: 'examples/queries' },
          ],
        },
        { label: 'Writing examples', slug: 'writing-examples' },
        { label: 'Changelog', slug: 'changelog' },
        { label: 'API reference', link: 'api/' },
      ],
    }),
  ],
});
