#!/usr/bin/env python3
"""spec-003 invariant: no shipped doc teaches the deprecated grain{} authoring model
without pointing the reader at the functional grain runtime.

Written as an invariant, not as a fixed list of files: it re-derives which docs mention
the deprecated cluster on every run, so a NEW un-signalled doc fails the build. Two
earlier rounds of this deprecation pass each asserted "the N docs that still teach the
grain{} model" and each was wrong -- a claim about a count cannot catch incompleteness.

Run: python3 scripts/check-deprecation-signal.py   (exit 1 on any un-signalled doc)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Constructs unique to the deprecated cluster. Deliberately NOT matched:
#   EventSourcedGrainDefinition / TransactionalGrainDefinition / AtmGrainDefinition
#     -- separate, non-deprecated types;
#   log templates such as "grain {GrainId}" -- the `{` must open a CE, not a placeholder.
OLD = re.compile(
    r'(?<![A-Za-z])grain\s*\{\s*(?:$|[a-z`\n])'
    r'|(?<![A-Za-z])grain \{ \}'
    r'|(?<![A-Za-z.])AddFSharpGrain'
    r'|FSharpGrain\.(?:ref|send|post|ask)'
    r'|FSharpGrainHandle|AdditionalStateSpec|UniversalGrainHandlerRegistry'
    r'|\[<FSharpGrain>\]|getFSharpGrain|withFSharpGrain'
    r'|(?<![A-Za-z])GrainDefinition<',
    re.M,
)

# The signal must name the replacement. Matching a bare "deprecated" would be too weak:
# README.md carried the sentence "keywords that were deprecated in 2.x" about an unrelated
# removal, which would have masked the fact that it had no grain{} deprecation notice.
SIG = re.compile(r'functional-grains|FunctionalGrain|AddFunctionalGrain|grainContract')

PATTERNS = [
    'README.md', 'DEVGUIDE.md', 'QUICK-REFERENCE.md', 'CONTRIBUTING.md',
    'docs/*.md',
    'website/src/content/docs/*.md', 'website/src/content/docs/*.mdx',
    'website/public/llms*.txt',
    'src/*/README.md', 'examples/*/README.md', 'samples/*/README.md',
    'testbed/README.md',
]

# CHANGELOG.md is exempt by design: it records what shipped in each release verbatim,
# and rewriting history to add forward references would make it a worse changelog.
EXEMPT = {'CHANGELOG.md'}


def main() -> int:
    targets = sorted({p for pat in PATTERNS for p in ROOT.glob(pat)})
    failures = []
    scanned = 0
    for path in targets:
        rel = path.relative_to(ROOT).as_posix()
        if rel in EXEMPT:
            continue
        scanned += 1
        text = path.read_text(encoding='utf-8')
        hits = OLD.findall(text)
        if hits and not SIG.search(text):
            failures.append((rel, len(hits), hits[:3]))

    for rel, n, sample in failures:
        print(f'FAIL {rel}: {n} deprecated-cluster mention(s) and no pointer to the '
              f'functional grain runtime; e.g. {sample}')
    print(f'checked {scanned} docs; {len(failures)} lacking a deprecation pointer')
    if failures:
        print('\nAdd a short note naming the replacement '
              '(grainContract / grainFor / FunctionalGrain.ref / AddFunctionalGrain) '
              'and linking docs/functional-grains.md, or add the file to EXEMPT with a reason.')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
