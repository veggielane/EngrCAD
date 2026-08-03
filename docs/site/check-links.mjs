// Validates every internal link and asset reference in the BUILT site, and exits nonzero
// naming each one that does not resolve.
//
// WHY NOT `starlight-links-validator`. It was tried first and cannot work here: it derives
// each page's id with `path.relative(srcDir + '/content/docs', filePath)` (libs/markdown.ts),
// one hard-coded assumption about where content lives. This project's markdown deliberately
// stays in `docs/` so EngrCAD.DocsGen's root, its fence syntax and every committed image
// path are untouched by the move to Starlight, so every id came out as `../../../<page>/`
// and all 134 internal links were reported invalid at once.
//
// Checking the OUTPUT rather than the markdown AST turned out to be the better instrument
// regardless, for three reasons:
//   * it resolves links exactly as a browser will, so a page that moved one level deeper
//     (which is precisely what DocFX -> Starlight did to every page) cannot pass;
//   * it covers <img>, <iframe> and raw-HTML hrefs, not just markdown link syntax -- the
//     example screenshots are the bulk of this site and they are references too;
//   * it checks fragments against the ids actually emitted, so a renamed heading fails.
//
// Anything outside the Starlight build (the DocFX API subtree at /api/, the WebAssembly
// demo at /live/) is merged into the same _site by CI and so is listed as external here.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, posix, relative, resolve } from 'node:path';

const dist = resolve(process.argv[2] ?? 'dist');
const base = (process.env.DOCS_BASE || '/').replace(/\/*$/, '/');

// Merged into the site by .github/workflows/docs.yml after this build, so they cannot
// exist in `dist` yet. Prefixes are matched against the resolved site path.
const external = ['api/', 'live/'];
// Pagefind ships its own UI templates containing `{{ … }}` placeholders; it is a generated
// search bundle rather than documentation, so it is not ours to validate.
const skipDirs = new Set(['pagefind']);

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      if (!skipDirs.has(name)) walk(full, out);
    } else out.push(full);
  }
  return out;
}

const files = walk(dist);
const present = new Set(files.map((f) => relative(dist, f).split('\\').join('/')));
const pages = files.filter((f) => f.endsWith('.html'));

// A code sample may CONTAIN an href — docs/examples/web.md quotes `<base href="./" />`
// as prose about the WebAssembly demo — and inside a code span markdown escapes `<` and
// `>` but not the quotes, so a bare attribute scan reads it as a link and reports a page
// that was never linked. Code is documentation ABOUT markup, never markup, so it is
// removed before the scan.
const stripCode = (html) => html.replace(/<(pre|code)\b[^>]*>[\s\S]*?<\/\1>/g, '');

/** The ids a page offers as link fragments. */
function idsOf(html) {
  const ids = new Set(['', '_top']);
  for (const m of html.matchAll(/\sid="([^"]+)"/g)) ids.add(m[1]);
  return ids;
}

const idCache = new Map();
function ids(sitePath) {
  if (!idCache.has(sitePath)) idCache.set(sitePath, idsOf(readFileSync(join(dist, sitePath), 'utf8')));
  // (ids are read from the unstripped page: a heading anchor is real markup.)
  return idCache.get(sitePath);
}

/** Resolves a site path to the file that serves it, or null. */
function fileFor(sitePath) {
  if (present.has(sitePath)) return sitePath;
  const asIndex = posix.join(sitePath, 'index.html');
  return present.has(asIndex) ? asIndex : null;
}

const problems = [];
for (const page of pages) {
  const pagePath = relative(dist, page).split('\\').join('/');
  const raw = readFileSync(page, 'utf8');
  const selfIds = idsOf(raw);
  const html = stripCode(raw);
  // The directory a relative reference resolves against, which is the page's own URL when
  // that URL ends in a slash. Taking `dirname` of it unconditionally would silently drop a
  // segment and make every `../` link look one level shallower than a browser sees it --
  // which is precisely the DocFX-to-Starlight breakage this file exists to catch.
  const pageUrl = base + pagePath.replace(/index\.html$/, '');
  const pageUrlDir = pageUrl.endsWith('/') ? pageUrl : `${posix.dirname(pageUrl)}/`;

  for (const [, attr, raw] of html.matchAll(/\s(href|src)="([^"]*)"/g)) {
    if (!raw || /^(?:[a-z][a-z0-9+.-]*:|\/\/|\{\{)/i.test(raw)) continue;

    const [target, hash = ''] = raw.split('#');
    const where = `${pagePath}: ${attr}="${raw}"`;

    if (target === '') {
      if (hash && !selfIds.has(hash)) problems.push(`${where} — no element with id "${hash}" on this page`);
      continue;
    }

    const cleaned = target.split('?')[0];
    const absolute = cleaned.startsWith('/')
      ? cleaned
      : posix.resolve(pageUrlDir, cleaned) + (cleaned.endsWith('/') ? '/' : '');

    if (!absolute.startsWith(base)) {
      problems.push(`${where} — resolves to ${absolute}, outside the site base ${base}`);
      continue;
    }
    const sitePath = absolute.slice(base.length);
    if (external.some((p) => sitePath.startsWith(p))) continue;

    const file = fileFor(sitePath.replace(/\/$/, ''));
    if (!file) {
      problems.push(`${where} — resolves to /${sitePath}, which the build did not emit`);
      continue;
    }
    if (hash && file.endsWith('.html') && !ids(file).has(hash)) {
      problems.push(`${where} — /${sitePath} has no element with id "${hash}"`);
    }
  }
}

// A page missing from the sidebar still builds and is still reachable by URL, so nothing
// complains — it is simply invisible, which is how a nav and a page set drift apart. The
// ordering used to live in docs/toc.yml and now lives in astro.config.mjs's `sidebar`;
// this asserts the two sets still agree, reading the nav out of the rendered artifact
// rather than re-deriving it (a second copy of the rule would agree with a broken sidebar
// as happily as with a correct one).
const home = readFileSync(join(dist, 'index.html'), 'utf8');
const nav = home.match(/<nav class="sidebar[^"]*"[\s\S]*?<\/nav>/)?.[0];
if (!nav) {
  problems.push('index.html — could not find the sidebar nav, so page coverage was not checked');
} else {
  const linked = new Set([...nav.matchAll(/href="([^"]*)"/g)].map((m) => m[1].replace(/#.*$/, '')));
  for (const page of pages) {
    const url = base + relative(dist, page).split('\\').join('/').replace(/index\.html$/, '');
    // The home page is the site title's link and 404 is not a route.
    if (url === base || url.endsWith('/404.html')) continue;
    if (!linked.has(url)) problems.push(`${url} — built, but not reachable from the sidebar in astro.config.mjs`);
  }
}

const checked = pages.length;
if (problems.length > 0) {
  console.error(`\nBroken references in the built site (${problems.length}):\n`);
  for (const p of problems) console.error(`  ${p}`);
  console.error(`\n${checked} pages checked against ${present.size} emitted files.\n`);
  process.exit(1);
}
console.log(`links: ${checked} pages checked against ${present.size} emitted files, all references resolve.`);
