// Turns the committed example screenshot into a POSTER with a "Run it" button, for the
// examples the documentation build could compile for the browser.
//
// WHY THE SCREENSHOT STAYS. The committed PNGs are this repository's regression oracle for
// anything that changes what a reader sees: an ambient-occlusion change moved 7 of 106, a
// 2D-stroke corner fix moved exactly the 2 stroke-derived figures, and a whole-render
// refactor was validated by all 108 being byte-identical. A live-only example page throws
// that away. The picture is also what the page is FOR — a reader scrolling a page of
// examples wants pictures, and megabytes of runtime per page view to look at one would be a
// bad trade for every reader who never clicks.
//
// So the viewer activates on a CLICK. The runtime is then cached by the browser for every
// other example the same reader opens, which is what makes the second one cheap.
//
// The iframe URL is RELATIVE and is built in the browser from a data attribute, never
// emitted as an href/src. Both halves matter: relative keeps the app path-portable (the
// site runs from a root, from /EngrCAD/ and from a local preview), and keeping it out of
// the markup keeps `check-links.mjs` -- which resolves every emitted href and src against
// the emitted files -- from failing on `live/`, a directory CI merges in afterwards and the
// Astro build never sees.
import { existsSync, readFileSync } from 'node:fs';
import { relative, resolve } from 'node:path';
import { visit } from 'unist-util-visit';

/**
 * Reads the manifest EngrCAD.DocsGen writes beside the markdown. Missing is not an error:
 * a checkout that has never run the docs generator still builds a correct site, it just
 * offers no Run buttons.
 * @param {string} contentRoot absolute path of the docs folder
 * @returns {{ live: Set<string>, total: number }}
 */
export function readLiveManifest(contentRoot) {
  const path = resolve(contentRoot, 'examples/live-examples.json');
  if (!existsSync(path)) return { live: new Set(), reasons: new Map(), total: 0 };
  const data = JSON.parse(readFileSync(path, 'utf8'));
  return {
    live: new Set(data.examples.filter((e) => e.live).map((e) => e.id)),
    // Why an example CANNOT run, verbatim from the manifest -- the boundary the site
    // used to state only to whoever opened the JSON.
    reasons: new Map(
      data.examples.filter((e) => !e.live && e.reason).map((e) => [e.id, e.reason])),
    total: data.examples.length,
  };
}

/** `examples/primitives.md` is served at `<base>examples/primitives/`, so the app at
 *  `<base>live/` is two levels up. Derived from the file path rather than assumed, because
 *  `getting-started.md` is one level and `index.md` is none. */
function upToBase(contentRoot, source) {
  const rel = relative(contentRoot, source).replaceAll('\\', '/');
  if (rel === 'index.md') return '';
  return '../'.repeat(rel.slice(0, -'.md'.length).split('/').length);
}

/**
 * @param {{ contentRoot: string, live: Set<string>, reasons?: Map<string, string> }} options
 */
export function liveExamples({ contentRoot, live, reasons }) {
  const why = reasons ?? new Map();
  return function rehypeLiveExamples() {
    return (tree, file) => {
      const source = file.path ?? file.history?.[0];
      if (!source || (live.size === 0 && why.size === 0)) return;
      const up = upToBase(contentRoot, source);

      visit(tree, 'element', (node, index, parent) => {
        if (node.tagName !== 'img' || !parent || index === null) return;
        const src = node.properties?.src;
        if (typeof src !== 'string') return;
        // The id is the file stem the fence declared. Astro fingerprints the asset URL, so
        // the emitted name may carry a hash -- match the leading stem, which the hash is
        // appended to, and never the whole basename.
        const id = (src.split('/').pop() ?? '').split('.')[0];
        if (!live.has(id)) {
          // A figure whose example cannot run says so, from the same manifest the Run
          // button reads -- a one-line caption rather than silence, so the boundary is
          // visible to the reader and keeps pressure on its causes. The compiler's own
          // words follow the short claim; a figure with no example at all (no manifest
          // entry) stays a plain screenshot.
          const reason = why.get(id);
          if (!reason) return;
          parent.children[index] = {
            type: 'element',
            tagName: 'figure',
            properties: { className: ['engrcad-live-static'] },
            children: [
              node,
              {
                type: 'element',
                tagName: 'figcaption',
                properties: { className: ['engrcad-live-note'], title: reason },
                children: [{
                  type: 'text',
                  value: 'This example runs on the full kernel only \u2014 '
                    + shortReason(reason),
                }],
              },
            ],
          };
          return;
        }

        parent.children[index] = {
          type: 'element',
          tagName: 'div',
          properties: {
            className: ['engrcad-live'],
            'data-example': id,
            'data-src': `${up}live/?example=${encodeURIComponent(id)}&embed`,
          },
          children: [
            node,
            {
              type: 'element',
              tagName: 'button',
              properties: { type: 'button', className: ['engrcad-live-run'] },
              children: [{ type: 'text', value: 'Run it in your browser' }],
            },
          ],
        };
      });
    };
  };
}

/** The caption's clause: the manifest's reason with its boilerplate prefix folded away
 *  (the full text rides on the title attribute for whoever hovers). */
function shortReason(reason) {
  const compile = 'does not compile against the browser\u0027s assemblies: ';
  if (reason.startsWith(compile)) {
    const message = reason.slice(compile.length);
    const name = message.match(/'([^']+)'/);
    return name
      ? `it uses ${name[1]}, which the browser build does not ship.`
      : 'it uses APIs the browser build does not ship.';
  }
  return reason.endsWith('.') ? reason : reason + '.';
}
