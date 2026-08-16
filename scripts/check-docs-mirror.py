#!/usr/bin/env python3
"""Fail if a docs/*.md page and its website/src/content/docs/ mirror have drifted.

The website copy is what the docs site publishes (.github/workflows/docs.yml builds
website/ and deploys website/dist). Nothing kept the two in sync, so an edit to docs/
could — and did — leave the shipped page saying something different: four deprecation
banners were added to docs/ only, and the live site kept teaching the deprecated model
with no signal at all.

The mirror is the docs/ file with a Starlight frontmatter block prepended, nothing else.
KNOWN_DRIFT records the pairs whose *content* had already diverged before this check
existed; reconciling those is a content question (which side is correct?), not a sync
question, so they are listed rather than silently ignored.
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / 'docs'
SITE = ROOT / 'website' / 'src' / 'content' / 'docs'

# Pre-existing content divergence, each pair carrying real differences beyond frontmatter.
# Shrink this set by reconciling a pair; never grow it to make a new edit pass.
KNOWN_DRIFT = {
    'api-reference.md',    # website copy documents a different onActivate/onDeactivate/
                           # EventSourcedGrainDefinition surface than docs/
    'event-sourcing.md',
    'redis-example.md',
    'testing.md',          # website copy uses EventSourcedGrainDefinition.handleCommand
}


def strip_frontmatter(text: str) -> str:
    if not text.startswith('---\n'):
        return text
    end = text.find('\n---\n', 4)
    if end == -1:
        return text
    return text[end + 5:].lstrip('\n')


def main() -> int:
    drifted, missing = [], []
    checked = 0
    for doc in sorted(DOCS.glob('*.md')):
        mirror = SITE / doc.name
        if not mirror.exists():
            missing.append(doc.name)
            continue
        checked += 1
        if doc.name in KNOWN_DRIFT:
            continue
        if strip_frontmatter(mirror.read_text(encoding='utf-8')) != doc.read_text(encoding='utf-8'):
            drifted.append(doc.name)

    for name in missing:
        print(f'MISSING MIRROR {name}: docs/{name} has no website/src/content/docs/{name}, '
              f'so the published site never shows it')
    for name in drifted:
        print(f'DRIFT {name}: docs/{name} and its published mirror differ beyond frontmatter — '
              f'`diff docs/{name} <(tail -n +6 website/src/content/docs/{name})`')
    print(f'checked {checked} doc/mirror pairs '
          f'({len(KNOWN_DRIFT)} exempt); {len(drifted)} drifted, {len(missing)} missing')
    return 1 if (drifted or missing) else 0


if __name__ == '__main__':
    sys.exit(main())
