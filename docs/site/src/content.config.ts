import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

// The documentation content deliberately lives OUTSIDE this Astro project, in `docs/`,
// exactly where it has always been:
//
//   * `tools/EngrCAD.DocsGen` scans `docs/**/*.md`, executes every tagged C# snippet and
//     writes the screenshots to `docs/examples/images/`. Its docs root is a command-line
//     argument, so it *could* follow the content anywhere -- but keeping the content put
//     means the generator's contract, the fence syntax and every committed image path are
//     untouched by the move to Starlight, which is what makes "no rendered pixel changed"
//     a meaningful check rather than a coincidence.
//   * The markdown stays readable in the repository at the path it has always had.
//
// So the collection uses Astro's `glob` loader with an explicit base rather than
// Starlight's `docsLoader()`, which assumes `src/content/docs/`. The loader still sets
// each entry's `filePath`, so relative image references (`images/<id>.png`) and relative
// links to sibling pages resolve against the markdown file's own directory as usual.
//
// `api/**` is excluded on purpose: that subtree is DocFX's (the generated .NET API
// reference), published as a static subtree at /api/ rather than as Starlight pages.
// The base is the REPOSITORY ROOT rather than `docs/`, for one file: `CHANGELOG.md` lives
// at the root because that is where every tool and every reader looks for it, and it is the
// single source of truth rather than a copy the site keeps in step. Raising the base is what
// lets the collection reach it; `generateId` then strips the `docs/` segment so every
// existing page keeps the id — and therefore the ROUTE — it already had. That equivalence is
// checked rather than assumed: the build reports the same page count and `check-links.mjs`
// resolves every reference, so a route that moved would fail here.
//
// The changelog's own links are written `docs/examples/<page>.md`, relative to the root, so
// they work on GitHub; `rewrite-doc-links.mjs` resolves them against the filesystem and maps
// them onto Starlight routes with no special case, because its `contentRoot` is still `docs/`
// and every link TARGET lives under it.
export const collections = {
  docs: defineCollection({
    loader: glob({
      base: '../../',
      pattern: [
        'docs/index.md',
        'docs/getting-started.md',
        'docs/writing-examples.md',
        'docs/examples/*.md',
        'CHANGELOG.md',
      ],
      generateId: ({ entry }) =>
        entry === 'CHANGELOG.md'
          ? 'changelog'
          : entry.replace(/^docs\//, '').replace(/\.md$/, ''),
    }),
    schema: docsSchema(),
  }),
};
