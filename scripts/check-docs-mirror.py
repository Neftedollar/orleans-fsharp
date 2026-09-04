#!/usr/bin/env python3
"""Fail if a docs/**/*.md page and its website/src/content/docs/ mirror have drifted.

The website copy is what the docs site publishes (.github/workflows/docs.yml builds
website/ and deploys website/dist). Nothing kept the two in sync, so an edit to docs/
could — and did — leave the shipped page saying something different: four deprecation
banners were added to docs/ only, and the live site kept teaching the deprecated model
with no signal at all.

The mirror is the docs/ file with a Starlight frontmatter block prepended and its
cross-page links in the site form, nothing else.

The link form is not cosmetic and is the reason this check is not plain byte-identity:
docs/ is rendered by GitHub straight from the repository, where
`[x](legacy/grain-definition.md)` is the working link and
`/orleans-fsharp/legacy/grain-definition/` is not; the built site is the
reverse -- Astro emits a relative .md href verbatim and never emits a .md file, so on the
site that same link 404s. Demanding byte-identity would therefore force one side to ship
dead links (it did: 67 of them). So both sides are normalised to the site form before
comparison, which keeps the full-content drift guarantee while letting each side carry the
link syntax that actually resolves where it is published.

KNOWN_DRIFT records the pairs whose *content* had already diverged before this check
existed; reconciling those is a content question (which side is correct?), not a sync
question, so they are listed rather than silently ignored.
"""
import posixpath
import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / 'docs'
SITE = ROOT / 'website' / 'src' / 'content' / 'docs'
SECURITY_CANONICAL = ROOT / 'SECURITY.md'
SECURITY_MIRROR = ROOT / '.github' / 'SECURITY.md'
IGNORED_DOC_TREES = {'superpowers'}  # local, gitignored planning artifacts; never published

# Pre-existing content divergence, each pair carrying real differences beyond frontmatter.
# Shrink this set by reconciling a pair; never grow it to make a new edit pass.
KNOWN_DRIFT: set[str] = set()


BASE = '/orleans-fsharp'
REL_MD_LINK = re.compile(r'\]\(((?!/)[^):\s]+)\.md(#[^)]*)?\)')
REL_PUBLIC_ASSET = re.compile(r'\]\(\.\./website/public/([^)]+)\)')


def normalise_links(text: str, source_dir: str = '') -> str:
    """Rewrite relative Markdown links to the equivalent absolute site route."""
    def replace(match: re.Match[str]) -> str:
        target = posixpath.normpath(posixpath.join(source_dir, match.group(1)))
        if target.endswith('/index'):
            target = target[:-len('/index')]
        return f']({BASE}/{target}/{match.group(2) or ""})'

    text = REL_PUBLIC_ASSET.sub(lambda match: f']({BASE}/{match.group(1)})', text)
    return REL_MD_LINK.sub(replace, text)


def strip_frontmatter(text: str) -> str:
    if not text.startswith('---\n'):
        return text
    end = text.find('\n---\n', 4)
    if end == -1:
        return text
    return text[end + 5:].lstrip('\n')


def keep_frontmatter(text: str) -> str | None:
    """Return one complete leading frontmatter block, ready to prepend to a body."""
    if not text.startswith('---\n'):
        return None
    end = text.find('\n---\n', 4)
    if end == -1:
        return None
    return text[:end + 5].rstrip('\n') + '\n\n'


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        '--write',
        action='store_true',
        help='replace each website mirror body from docs/ while preserving its frontmatter',
    )
    args = parser.parse_args()

    drifted, missing = [], []
    policy_failures = []
    written = 0
    checked = 0
    for doc in sorted(DOCS.rglob('*.md')):
        relative = doc.relative_to(DOCS)
        if relative.parts[0] in IGNORED_DOC_TREES:
            continue
        name = relative.as_posix()
        mirror = SITE / relative
        if not mirror.exists():
            missing.append(name)
            continue
        checked += 1
        source_dir = relative.parent.as_posix()
        if args.write:
            mirror_text = mirror.read_text(encoding='utf-8')
            frontmatter = keep_frontmatter(mirror_text)
            if frontmatter is None:
                print(f'INVALID MIRROR {name}: missing or unterminated Starlight frontmatter')
                missing.append(name)
                continue
            site_body = normalise_links(doc.read_text(encoding='utf-8'), source_dir)
            updated = frontmatter + site_body
            if updated != mirror_text:
                mirror.write_text(updated, encoding='utf-8')
                written += 1
        if name in KNOWN_DRIFT:
            continue
        left = normalise_links(strip_frontmatter(mirror.read_text(encoding='utf-8')), source_dir)
        right = normalise_links(doc.read_text(encoding='utf-8'), source_dir)
        if left != right:
            drifted.append(name)

        if relative.parts[0] == 'legacy':
            status = doc.read_text(encoding='utf-8').lower()
            status = re.sub(r'(?m)^>\s?', '', status)
            required = (
                ('archived and unsupported', r'archived\s+and\s+unsupported'),
                ('no new Legacy release line', r'no\s+new\s+legacy\s+release\s+line'),
                ('security fixes', r'security\s+fixes'),
            )
            absent = [label for label, pattern in required if re.search(pattern, status) is None]
            if absent:
                policy_failures.append(
                    f'LEGACY STATUS {name}: missing {", ".join(repr(value) for value in absent)}'
                )

    canonical_security = SECURITY_CANONICAL.read_text(encoding='utf-8')
    mirrored_security = SECURITY_MIRROR.read_text(encoding='utf-8')
    if args.write and canonical_security != mirrored_security:
        SECURITY_MIRROR.write_text(canonical_security, encoding='utf-8')
        mirrored_security = canonical_security
        print('updated .github/SECURITY.md from canonical SECURITY.md')
    if canonical_security != mirrored_security:
        policy_failures.append(
            'SECURITY MIRROR: .github/SECURITY.md differs from canonical SECURITY.md'
        )

    for name in missing:
        print(f'MISSING MIRROR {name}: docs/{name} has no website/src/content/docs/{name}, '
              f'so the published site never shows it')
    for name in drifted:
        print(f'DRIFT {name}: docs/{name} and its published mirror differ beyond frontmatter '
              f'and link form — `diff docs/{name} <(tail -n +6 website/src/content/docs/{name})` '
              f'(ignore the `](page.md)` vs `](/orleans-fsharp/page/)` lines, those are expected)')
    for failure in policy_failures:
        print(failure)
    print(f'checked {checked} doc/mirror pairs '
          f'({len(KNOWN_DRIFT)} exempt); {len(drifted)} drifted, {len(missing)} missing, '
          f'{len(policy_failures)} policy failure(s)')
    if args.write:
        print(f'updated {written} website mirror(s) from docs/')
    return 1 if (drifted or missing or policy_failures) else 0


if __name__ == '__main__':
    sys.exit(main())
