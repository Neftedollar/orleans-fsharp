#!/usr/bin/env python3
"""Fail when a current documentation page starts teaching a legacy authoring API.

Legacy pages are intentionally excluded. A link to the Legacy section is allowed; executable
examples and registration calls from the original authoring surface are not.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOC_ROOTS = (
    ROOT / 'docs',
    ROOT / 'website' / 'src' / 'content' / 'docs',
)

LEGACY_EXAMPLES = re.compile(
    r'(?m)^\s*(?:let\s+[A-Za-z0-9_\']+\s*=\s*)?grain\s*\{'
    r'|\beventSourcedGrain\s*\{'
    r'|\bAddFSharpGrain(?:sFromAssembly)?\b'
    r'|\bFSharpGrain\.(?:ref|send|post|ask)\b'
    r'|\bGrainDefinition\.(?:get|handle|fold|apply)'
    r'|\bGrainContext\.(?:get|primary|grainId|deactivate|delay)'
    r'|\bUniversalGrainHandlerRegistry\b'
    r'|\bwithFSharpGrain(?:Guid|Int)?\b'
)


def is_current_page(path: Path, root: Path) -> bool:
    relative = path.relative_to(root)
    return 'legacy' not in relative.parts and 'superpowers' not in relative.parts


def main() -> int:
    failures: list[tuple[str, int, str]] = []
    scanned = 0

    for root in DOC_ROOTS:
        for path in sorted((*root.rglob('*.md'), *root.rglob('*.mdx'))):
            if not is_current_page(path, root):
                continue
            scanned += 1
            text = path.read_text(encoding='utf-8')
            for match in LEGACY_EXAMPLES.finditer(text):
                line = text.count('\n', 0, match.start()) + 1
                sample = match.group(0).strip().replace('\n', ' ')
                failures.append((path.relative_to(ROOT).as_posix(), line, sample))

    for path, line, sample in failures:
        print(f'LEGACY API IN CURRENT DOC {path}:{line}: {sample}')

    print(f'checked {scanned} current documentation pages; {len(failures)} legacy example(s)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
