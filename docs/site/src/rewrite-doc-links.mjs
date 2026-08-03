// Rewrites the relative `*.md` links the documentation is written with into the routes
// Starlight actually serves.
//
// WHY A PLUGIN RATHER THAN REWRITING 100+ LINKS IN 54 FILES. DocFX served
// `examples/fields.html`, so a sibling page was `fields.md` and a browser resolved it.
// Starlight serves `examples/fields/`, so the same href would 404 -- and it would do so
// SILENTLY, which is the one failure mode a documentation migration must not have.
// Rewriting every link by hand would fix it and cost two things worth keeping: the
// markdown stops being navigable in the repository and on GitHub, and every link gains an
// extra `../` that a future contributor has to remember. One rule in one place does the
// same job, and it does something hand-rewriting cannot: it RESOLVES each target against
// the filesystem and throws when the file is not there, so a link to a page that was
// renamed or deleted fails the build naming both ends.
//
// The emitted URL is site-absolute and carries the configured `base`, which is what lets
// `starlight-links-validator` check the result -- a relative href is unverifiable, and an
// unverified link is how this class of rot starts.
import { existsSync } from 'node:fs';
import { dirname, resolve, relative } from 'node:path';
import { visit } from 'unist-util-visit';

/** Splits `page.md#anchor` into its path and its trailing `#anchor` / `?query`. */
function splitTarget(href) {
  const cut = href.search(/[#?]/);
  return cut < 0 ? [href, ''] : [href.slice(0, cut), href.slice(cut)];
}

/**
 * @param {{ contentRoot: string, base: string }} options
 *   contentRoot — the absolute path of the docs folder the collection globs.
 *   base — Astro's configured base path, e.g. `/` or `/EngrCAD/`.
 */
export function rewriteDocLinks({ contentRoot, base }) {
  const prefix = base.endsWith('/') ? base : `${base}/`;

  return function rehypeRewriteDocLinks() {
    return (tree, file) => {
      const source = file.path ?? file.history?.[0];
      if (!source) return;

      visit(tree, 'element', (node) => {
        if (node.tagName !== 'a') return;
        const href = node.properties?.href;
        if (typeof href !== 'string') return;
        // Absolute, protocol-relative, in-page and already-rooted links are the author's.
        if (/^(?:[a-z][a-z0-9+.-]*:|\/\/|[#/])/i.test(href)) return;

        const [path, suffix] = splitTarget(href);
        if (!path.endsWith('.md')) return;

        const target = resolve(dirname(source), path);
        if (!existsSync(target)) {
          throw new Error(
            `${source}: link "${href}" points at ${target}, which does not exist. ` +
              'Documentation links are resolved against the filesystem, so a renamed or ' +
              'deleted page fails the build here rather than 404ing on the site.',
          );
        }

        const rel = relative(contentRoot, target).replaceAll('\\', '/');
        // `api/**` is DocFX's generated .NET reference, merged into the same site at
        // /api/ by CI. It is a real file on disk but not a Starlight page, so it maps to
        // the subtree URL rather than to a route.
        node.properties.href = rel.startsWith('api/')
          ? `${prefix}api/${suffix}`
          : rel === 'index.md'
            ? `${prefix}${suffix}`
            : `${prefix}${rel.slice(0, -'.md'.length)}/${suffix}`;
      });
    };
  };
}
