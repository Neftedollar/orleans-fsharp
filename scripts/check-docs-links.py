#!/usr/bin/env python3
"""Fail if the built docs site contains an internal link to a page it does not emit.

Runs against website/dist after `npm run build`.

Two classes of dead link, both observed live in this repository:

1. Absolute hrefs under the base path that point nowhere -- Astro does not validate
   them, which is how five /orleans-fsharp/guides/* links stayed broken on the site.

2. Relative `.md` hrefs. This is the one the first version of this script asserted
   away: its docstring claimed "Astro/Starlight resolves relative .md links at build
   time", and it skipped every href that did not start with the base path. Neither
   holds. This Astro 6 / Starlight 0.38 setup emits `<a href="functional-grains.md">`
   verbatim, `find website/dist -name '*.md'` is empty, and from /orleans-fsharp/testing/
   that href resolves to /orleans-fsharp/testing/functional-grains.md -- a 404. The
   check ran green over 67 such links in the artifact it had just scanned.
   So: resolve every non-anchor relative href against the emitting page's own
   directory and require the target to exist in dist.

Inline <script>/<style> bodies are stripped first -- the theme's own JS contains
template literals like href="${n}" that are not links.
"""
import re
import sys
from pathlib import Path
from urllib.parse import urljoin

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / 'website' / 'dist'
BASE = '/orleans-fsharp'

SCRIPTISH = re.compile(r'<(script|style)\b[^>]*>.*?</\1>', re.S | re.I)
HREF = re.compile(r'href="([^"]+)"')
EXTERNAL = re.compile(r'^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|//)')


def emitted(rel: str) -> bool:
    """Does dist actually contain something served at this base-relative path?"""
    rel = rel.strip('/')
    if not rel:
        return True
    return any((DIST / c).exists() for c in (rel, rel + '/index.html', rel + '.html'))


def main() -> int:
    if not DIST.is_dir():
        print(f'{DIST} not found — run `npm run build` in website/ first')
        return 1

    broken: dict[str, set[str]] = {}
    pages = list(DIST.rglob('*.html'))
    checked_links = 0
    for page in pages:
        html = SCRIPTISH.sub('', page.read_text(encoding='utf-8', errors='replace'))
        # URL of this page as the browser sees it, so relative hrefs resolve the same way.
        page_url = f'{BASE}/{page.relative_to(DIST).parent.as_posix()}/'.replace('/./', '/')
        for href in HREF.findall(html):
            if EXTERNAL.match(href) or href.startswith('#') or not href.strip():
                continue
            target = href.split('#')[0].split('?')[0]
            if not target:
                continue
            resolved = target if target.startswith('/') else urljoin(page_url, target)
            checked_links += 1
            if not resolved.startswith(BASE + '/') and resolved != BASE:
                # Root-absolute link outside the configured base: 404 on the deployed site.
                broken.setdefault(f'{href} (outside base {BASE})', set()).add(
                    page.relative_to(DIST).as_posix())
                continue
            if not emitted(resolved[len(BASE):]):
                label = href if href.startswith('/') else f'{href} -> {resolved}'
                broken.setdefault(label, set()).add(page.relative_to(DIST).as_posix())

    for target in sorted(broken):
        print(f'BROKEN {target} <- {sorted(broken[target])}')
    print(f'checked {len(pages)} built pages, {checked_links} internal link(s); '
          f'{len(broken)} broken internal target(s)')
    if broken:
        print('\nInside website/src/content/docs use the site form '
              '`/orleans-fsharp/<page>/`; a relative `<page>.md` link is correct only in '
              'docs/, which GitHub renders directly.')
    return 1 if broken else 0


if __name__ == '__main__':
    sys.exit(main())
