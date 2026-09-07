# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Orleans.FSharp, please report it responsibly.

**Do NOT open a public GitHub issue for security vulnerabilities.**

Instead, email: **security@neftedollar.dev** (or use GitHub's private vulnerability reporting if enabled on this repo).

Include:
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

We will acknowledge your report within 48 hours and provide a fix timeline.

## Supported Versions

Security support applies to the current functional API only.

| Version / branch | Supported | Notes |
|---|---|---|
| `5.0.x` | Yes | Current published stable line |
| `4.1.x` | Yes | Previous stable line; functional API only |
| `main` | Yes | Development branch; changes after the release tag may be unreleased |
| `4.0.x` and older | No | Upgrade to 5.0.0 or another supported release |
| Legacy authoring models | No | Archived; no new Legacy release line or security fixes |

`SECURITY.md` at the repository root is canonical. `.github/SECURITY.md` is an exact mirror for
GitHub discovery and must be updated in the same change.
