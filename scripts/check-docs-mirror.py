#!/usr/bin/env python3
"""Fail if a docs/*.md page and its website/src/content/docs/ mirror have drifted.

The website copy is what the docs site publishes (.github/workflows/docs.yml builds
website/ and deploys website/dist). Nothing kept the two in sync, so an edit to docs/
could — and did — leave the shipped page saying something different: four deprecation
banners were added to docs/ only, and the live site kept teaching the deprecated model
with no signal at all.

The mirror is the docs/ file with a Starlight frontmatter block prepended and its
cross-page links in the site form, nothing else.

The link form is not cosmetic and is the reason this check is not plain byte-identity:
docs/ is rendered by GitHub straight from the repository, where `[x](grain-definition.md)`
is the working link and `/orleans-fsharp/grain-definition/` is not; the built site is the
reverse -- Astro emits a relative .md href verbatim and never emits a .md file, so on the
site that same link 404s. Demanding byte-identity would therefore force one side to ship
dead links (it did: 67 of them). So both sides are normalised to the site form before
comparison, which keeps the full-content drift guarantee while letting each side carry the
link syntax that actually resolves where it is published.

KNOWN_DRIFT records the pairs whose *content* had already diverged before this check
existed; reconciling those is a content question (which side is correct?), not a sync
question, so they are listed rather than silently ignored.
"""
import re
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


BASE = '/orleans-fsharp'
REL_MD_LINK = re.compile(r'\]\(([a-z0-9-]+)\.md(#[^)]*)?\)')


def normalise_links(text: str) -> str:
    """Rewrite `](page.md)` to the site form so both sides compare on content, not syntax."""
    return REL_MD_LINK.sub(lambda m: f']({BASE}/{m.group(1)}/{m.group(2) or ""})', text)


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
        left = normalise_links(strip_frontmatter(mirror.read_text(encoding='utf-8')))
        right = normalise_links(doc.read_text(encoding='utf-8'))
        if left != right:
            drifted.append(doc.name)

    for name in missing:
        print(f'MISSING MIRROR {name}: docs/{name} has no website/src/content/docs/{name}, '
              f'so the published site never shows it')
    for name in drifted:
        print(f'DRIFT {name}: docs/{name} and its published mirror differ beyond frontmatter '
              f'and link form — `diff docs/{name} <(tail -n +6 website/src/content/docs/{name})` '
              f'(ignore the `](page.md)` vs `](/orleans-fsharp/page/)` lines, those are expected)')
    print(f'checked {checked} doc/mirror pairs '
          f'({len(KNOWN_DRIFT)} exempt); {len(drifted)} drifted, {len(missing)} missing')
    return 1 if (drifted or missing) else 0


if __name__ == '__main__':
    sys.exit(main())
