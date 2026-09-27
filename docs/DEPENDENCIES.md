# Dependency review — 28 September 2026

Preview.3 uses Python's standard-library SQLite for this bounded lexical demo.
The previous application graph had 95 packages and 50 distinct advisory identities;
the development graph had 101 packages and 51. Advisory aliases and repeated records
are not counted as separate vulnerabilities. The affected packages included Chroma,
pypdf and transitively Starlette, with pytest additionally affected in development.
This audit is a dated package inventory, not an exploitation result or a guarantee
that every possible vulnerability has been found.

Published Chroma versions, including 1.5.9, had no patched version for
[GHSA-36p7-vc44-83pf](https://github.com/advisories/GHSA-36p7-vc44-83pf) and
[GHSA-2wm9-hf6c-p5cr](https://github.com/advisories/GHSA-2wm9-hf6c-p5cr) at review time.
The sample does not need Chroma's remote server or embedding functions. Replacing
that backend also removes NumPy and PostHog from its application graph. This does
not claim the earlier embedded flow was exploited, or that changing its version
alone would repair these advisories. Earlier indexes and packages are preserved.

The reviewed direct application pins are FastAPI 0.141.1, Starlette 1.7.0,
Uvicorn 0.34.0, pypdf 6.19.0 and Pydantic 2.10.6. The development pins add
httpx2 2.13.1, pytest 9.1.1 and ReportLab 5.0.0. Official
[PyPI](https://pypi.org/) resolution of the HTTPX2 pins produced 26 application/development
packages using Python 3.12.14, and 28 using Python 3.10.6. Both complete resolved
graphs were checked with [pip-audit](https://github.com/pypa/pip-audit) using the
PyPI advisory service: zero reported advisories, zero skipped packages for these
snapshots. Installer,
Python/SQLite, .NET and separately chosen model/runtime security are outside
that application dependency result. Installer bootstrap is reviewed separately:
`requirements-bootstrap.txt` pins pip 26.2.1 and setuptools 84.0.0, whose separate
dated two-package audit reported zero advisories and zero skips. Upgrade only the
new virtual environment before installing the application. The pins are not a
complete cross-platform lock. CI declares a strict combined dependency audit;
its remote result still requires observation.

Python 3.12 is the new default. Python 3.10 reaches the end of its scheduled
support in October 2026 ([official version status](https://devguide.python.org/versions/));
its earlier local evidence is a compatibility snapshot, not a recommendation to
keep using an old interpreter. Python/SQLite build security remains separate from
PyPI package advisories.

Install into a fresh virtual environment; do not reuse the old Chroma environment.
Run the source tests and extractive evaluation after any dependency change.
The declared CI matrix is not an observed Windows/Linux CI run, and a fresh local
virtual environment on the development computer is not a clean machine.
Keep future audits and dependency updates separate from real-model quality checks.
