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
export const collections = {
  docs: defineCollection({
    loader: glob({
      base: '../',
      pattern: ['index.md', 'getting-started.md', 'writing-examples.md', 'examples/*.md'],
    }),
    schema: docsSchema(),
  }),
};
