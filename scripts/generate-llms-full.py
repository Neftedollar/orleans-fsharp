#!/usr/bin/env python3
"""Generate the complete LLM documentation corpus in its published order.

The ordinary documentation source is ``docs/`` because CI already proves that its website mirror
is identical. The splash homepage and comparison page exist only in the website tree, so they are
the two explicit exceptions.
"""

from __future__ import annotations

import argparse
import posixpath
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / 'docs'
WEBSITE_DOCS = ROOT / 'website' / 'src' / 'content' / 'docs'
OUTPUT = ROOT / 'website' / 'public' / 'llms-full.txt'
BASE_URL = 'https://neftedollar.com/orleans-fsharp'
BASE_PATH = '/orleans-fsharp'

MARKDOWN_TARGET = re.compile(
    r'(?P<prefix>!?\[[^\]]*\]\()'
    r'(?P<target><[^>\n]+>|[^)\s]+)'
    r'(?P<suffix>(?:\s+(?:"[^"]*"|\'[^\']*\'))?\))'
)

CURRENT_ORDER = [
    'getting-started.md',
    'release-status.md',
    'how-to.md',
    'examples.md',
    'functional-runtime.md',
    'functional-grains/contracts.md',
    'functional-grains/state-and-lifecycle.md',
    'functional-grains/delivery-and-streaming.md',
    'functional-grains/placement-and-transactions.md',
    'functional-grains.md',
    'serialization.md',
    'streaming.md',
    'event-sourcing.md',
    'streaming-replies.md',
    'silo-configuration.md',
    'client-configuration.md',
    'dashboard.md',
    'testing.md',
    'security.md',
    'resilience.md',
    'advanced.md',
    'analyzers.md',
    'calling-from-csharp.md',
    'compatibility.md',
    'api-reference.md',
    'faq.md',
]


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


def route_url(route: str, *, query: str = '', fragment: str = '') -> str:
    clean_route = route.strip('/')
    result = f'{BASE_URL}/{clean_route}/' if clean_route else f'{BASE_URL}/'
    if query:
        result += f'?{query}'
    if fragment:
        result += f'#{fragment}'
    return result


def markdown_route(source_name: str, target_path: str) -> str:
    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(source_name), target_path))
    if resolved == '..' or resolved.startswith('../'):
        raise ValueError(f"documentation link escapes docs root: {source_name} -> {target_path}")
    without_suffix = resolved.removesuffix('.md').removesuffix('.mdx')
    if without_suffix == 'index':
        return ''
    if without_suffix.endswith('/index'):
        return without_suffix.removesuffix('/index')
    return without_suffix


def canonicalize_links(content: str, *, source_name: str, route: str) -> str:
    """Turn docs-relative Markdown links into canonical published URLs for the flat corpus."""

    def replace(match: re.Match[str]) -> str:
        wrapped = match.group('target')
        target = wrapped[1:-1] if wrapped.startswith('<') and wrapped.endswith('>') else wrapped
        parsed = urlsplit(target)

        if parsed.scheme or parsed.netloc:
            replacement = target
        elif parsed.path.startswith(BASE_PATH + '/') or parsed.path == BASE_PATH:
            suffix = parsed.path[len(BASE_PATH):]
            replacement = f'{BASE_URL}{suffix}'
            if parsed.query:
                replacement += f'?{parsed.query}'
            if parsed.fragment:
                replacement += f'#{parsed.fragment}'
        elif not parsed.path and parsed.fragment:
            replacement = route_url(route, fragment=parsed.fragment)
        elif parsed.path.endswith(('.md', '.mdx')):
            replacement = route_url(
                markdown_route(source_name, parsed.path),
                query=parsed.query,
                fragment=parsed.fragment,
            )
        else:
            normalized = posixpath.normpath(
                posixpath.join(posixpath.dirname(source_name), parsed.path)
            )
            while normalized.startswith('../'):
                normalized = normalized[3:]

            public_prefix = 'website/public/'
            if normalized.startswith(public_prefix):
                replacement = f'{BASE_URL}/{normalized[len(public_prefix):]}'
                if parsed.query:
                    replacement += f'?{parsed.query}'
                if parsed.fragment:
                    replacement += f'#{parsed.fragment}'
            else:
                replacement = target

        if wrapped.startswith('<') and wrapped.endswith('>'):
            replacement = f'<{replacement}>'
        return f"{match.group('prefix')}{replacement}{match.group('suffix')}"

    return MARKDOWN_TARGET.sub(replace, content)


def generate() -> str:
    chunks = [
        '# Orleans.FSharp — Full Documentation\n\n',
        f'Canonical site: {BASE_URL}/\n',
        'Repository: https://github.com/Neftedollar/orleans-fsharp\n',
        'Published stable: Orleans.FSharp 5.0.0. Documentation channel: '
        'Orleans.FSharp 5.0 stable.\n',
        'This file contains the complete published documentation. Current functional API pages '
        'come first. Unsupported migration material is isolated under the Legacy Archive heading '
        'near the end.\n\n',
        '## Author\n\n',
        '- [Roman Melnikov (Neftedollar)](https://neftedollar.com/)\n\n',
    ]

    homepage = (WEBSITE_DOCS / 'index.mdx').read_text(encoding='utf-8')
    chunks.append(
        source(
            '',
            canonicalize_links(
                without_frontmatter(homepage, remove_imports=True),
                source_name='index.mdx',
                route='',
            ),
        )
    )

    current = {
        path.relative_to(DOCS).as_posix(): path
        for path in DOCS.rglob('*.md')
        if path.relative_to(DOCS).parts[0] not in {'legacy', 'superpowers'}
    }

    ordered = [name for name in CURRENT_ORDER if name in current]
    ordered.extend(sorted(set(current) - set(ordered)))

    for name in ordered:
        path = current[name]
        route = name.removesuffix('.md')
        chunks.append(
            source(
                route,
                canonicalize_links(
                    path.read_text(encoding='utf-8'),
                    source_name=name,
                    route=route,
                ),
            )
        )

    comparison = (WEBSITE_DOCS / 'comparison.md').read_text(encoding='utf-8')
    chunks.append(
        source(
            'comparison',
            canonicalize_links(
                without_frontmatter(comparison),
                source_name='comparison.md',
                route='comparison',
            ),
        )
    )

    chunks.append(
        '\n\n# Legacy Archive — Unsupported Migration Documentation\n\n'
        'The following pages are archived and unsupported. There is no new Legacy release line, '
        'compatibility work, or security support. They are intentionally separated from the '
        'current functional documentation above.\n'
    )

    for path in sorted((DOCS / 'legacy').glob('*.md')):
        route = 'legacy' if path.stem == 'index' else f'legacy/{path.stem}'
        source_name = path.relative_to(DOCS).as_posix()
        chunks.append(
            source(
                route,
                canonicalize_links(
                    path.read_text(encoding='utf-8'),
                    source_name=source_name,
                    route=route,
                ),
            )
        )

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
