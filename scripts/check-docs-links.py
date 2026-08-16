#!/usr/bin/env python3
"""Fail if the built docs site contains an internal link to a page it does not emit.

Runs against website/dist after `npm run build`. Astro/Starlight resolves relative .md
links at build time but does not fail on an absolute href that points nowhere, which is
how five /orleans-fsharp/guides/* links stayed broken on the live site.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / 'website' / 'dist'
BASE = '/orleans-fsharp'


def main() -> int:
    if not DIST.is_dir():
        print(f'{DIST} not found — run `npm run build` in website/ first')
        return 1

    broken: dict[str, set[str]] = {}
    pages = list(DIST.rglob('*.html'))
    for page in pages:
        html = page.read_text(encoding='utf-8', errors='replace')
        for href in re.findall(r'href="([^"]+)"', html):
            if not href.startswith(BASE + '/'):
                continue
            target = href.split('#')[0].split('?')[0]
            rel = target[len(BASE) + 1:].rstrip('/')
            if not rel:
                continue
            if any((DIST / c).exists() for c in (rel, rel + '/index.html', rel + '.html')):
                continue
            broken.setdefault(target, set()).add(page.relative_to(DIST).as_posix())

    for target in sorted(broken):
        print(f'BROKEN {target} <- {sorted(broken[target])}')
    print(f'checked {len(pages)} built pages; {len(broken)} broken internal target(s)')
    return 1 if broken else 0


if __name__ == '__main__':
    sys.exit(main())
