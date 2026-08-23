#!/usr/bin/env python3
"""Generate the complete LLM documentation corpus in its published order.

The ordinary documentation source is ``docs/`` because CI already proves that its website mirror
is identical. The splash homepage and comparison page exist only in the website tree, so they are
the two explicit exceptions.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / 'docs'
WEBSITE_DOCS = ROOT / 'website' / 'src' / 'content' / 'docs'
OUTPUT = ROOT / 'website' / 'public' / 'llms-full.txt'
BASE_URL = 'https://neftedollar.com/orleans-fsharp'


def without_frontmatter(text: str, *, remove_imports: bool = False) -> str:
    """Remove one leading YAML frontmatter block and optional MDX import lines."""
    lines = text.splitlines(keepends=True)
    start = 0

    if lines and lines[0].strip() == '---':
        for index, line in enumerate(lines[1:], start=1):
            if line.strip() == '---':
                start = index + 1
                break
        else:
            raise ValueError('unterminated YAML frontmatter')

    body = lines[start:]
    if remove_imports:
        body = [line for line in body if not line.startswith('import ')]
    return ''.join(body)


def source(route: str, content: str) -> str:
    suffix = f'/{route.strip("/")}' if route.strip('/') else ''
    return f'\n\n--- Source: {BASE_URL}{suffix}/ ---\n\n{content}'


def generate() -> str:
    chunks = [
        '# Orleans.FSharp — Full Documentation\n\n',
        f'Canonical site: {BASE_URL}/\n',
        'Repository: https://github.com/Neftedollar/orleans-fsharp\n',
        'This file contains the complete published documentation. Current functional API pages '
        'come first. Compatibility material is isolated under the Legacy API heading near the '
        'end.\n\n',
    ]

    homepage = (WEBSITE_DOCS / 'index.mdx').read_text(encoding='utf-8')
    chunks.append(source('', without_frontmatter(homepage, remove_imports=True)))

    for path in sorted(DOCS.glob('*.md')):
        chunks.append(source(path.stem, path.read_text(encoding='utf-8')))

    comparison = (WEBSITE_DOCS / 'comparison.md').read_text(encoding='utf-8')
    chunks.append(source('comparison', without_frontmatter(comparison)))

    chunks.append(
        '\n\n# Legacy API — Compatibility Documentation\n\n'
        'The following pages describe obsolete compatibility APIs. They are intentionally '
        'separated from the current functional documentation above.\n'
    )

    for path in sorted((DOCS / 'legacy').glob('*.md')):
        route = 'legacy' if path.stem == 'index' else f'legacy/{path.stem}'
        chunks.append(source(route, path.read_text(encoding='utf-8')))

    return ''.join(chunks)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        '--check',
        action='store_true',
        help='fail instead of writing when llms-full.txt differs from its sources',
    )
    args = parser.parse_args()
    generated = generate()

    if args.check:
        current = OUTPUT.read_text(encoding='utf-8') if OUTPUT.exists() else None
        if current != generated:
            print('website/public/llms-full.txt is stale; run scripts/generate-llms-full.py')
            return 1
        print(f'llms-full.txt matches {generated.count("--- Source:")} source pages')
        return 0

    OUTPUT.write_text(generated, encoding='utf-8')
    print(f'wrote {OUTPUT.relative_to(ROOT)} from {generated.count("--- Source:")} source pages')
    return 0


if __name__ == '__main__':
    sys.exit(main())
