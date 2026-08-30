# Orleans.FSharp documentation site

This Starlight project publishes the current and Legacy Orleans.FSharp documentation at
`/orleans-fsharp/`.

## Source layout

- `../docs/` is the repository-readable Markdown source.
- `src/content/docs/` is the published mirror with Starlight frontmatter and site-form links.
- `src/content/docs/index.mdx` is the site-only landing page.
- `public/` contains screenshots, metadata files, and the generated LLM corpus.
- `astro.config.mjs` owns navigation and redirects.

The two Markdown trees intentionally contain the same prose. Edit `../docs/`, then refresh all
published bodies while preserving their Starlight frontmatter:

```bash
python3 scripts/check-docs-mirror.py --write
```

The ordinary check allows only frontmatter and link-syntax differences.

The local `DocBreadcrumb` component suppresses Starlight's generated page title because every
mirrored Markdown body already carries its own H1 for GitHub rendering. Removing that override
reintroduces duplicate titles and descriptions on every page.

## Local preview

From `website/`:

```bash
npm ci
npm run dev
```

The production build is:

```bash
npm run build
```

## Required checks

Run these from the repository root:

```bash
python3 scripts/check-docs-mirror.py
python3 scripts/check-current-docs-api.py
python3 scripts/check-deprecation-signal.py
python3 scripts/generate-llms-full.py --check
npm --prefix website run build
python3 scripts/check-docs-links.py
```

The checks cover source/site drift, accidental Legacy API examples in current docs, missing
deprecation pointers, the generated LLM corpus, the Starlight build, and emitted internal links.

## Adding a page

1. Add the Markdown source under `../docs/`.
2. Add a mirror under `src/content/docs/` with `title` and `description` frontmatter.
3. Run `python3 scripts/check-docs-mirror.py --write` from the repository root.
4. Add the page to the appropriate group in `astro.config.mjs`.
5. Link it from a nearby overview, recipe, or example page.
6. Run every required check above.

Keep tutorials, task recipes, conceptual explanations, and exhaustive references separate. A new
feature should have one canonical runnable example; other pages link to it instead of drifting
copies.

## Deployment

`.github/workflows/docs.yml` builds this directory and deploys `dist/` on pushes to `main`
which touch `website/**` or `docs/**`. Local builds do not publish anything.
