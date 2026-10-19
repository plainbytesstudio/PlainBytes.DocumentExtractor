# Changelog

All notable changes to PlainBytes DocumentExtractor are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project aims to follow [Semantic Versioning](https://semver.org/) for application releases.

### Corresponding Source

When an official Microsoft Store package is published, tag this repository (and preferably create a GitHub Release) with the complete four-part Store package version—for example Store `1.0.3.0` → `v1.0.3.0`. A tag alone does not establish GPLv3 Corresponding Source; the tagged tree must include the materials required to build and modify the application. The in-app tool version shown in About may use a shorter form; use the Store package version when matching tags.

See the [Corresponding Source](./README.md#corresponding-source-store-builds) note in `README.md`.

## [Unreleased]

## [1.0.0] — 2026-10-2

Initial public application release.

### Added

- Visual extraction of named fields and table regions from text-based PDFs
- Batch processing with one shared extraction configuration across similar layouts
- Reusable templates (local store; import/export as `.json`)
- Excel (`.xlsx`) export with a summary sheet and per-table detail sheets
- Local, offline extraction on Windows (source PDFs are not modified)
- In-app help center
- Microsoft Store distribution with a 7-day trial and one-time purchase unlock for the official packaged build
- Multilingual UI resources (including en, zh-Hans, zh-Hant, ja, ko, de, fr, es, it, pt, nl, ru, ar)

### Notes

- Scanned / image-only PDFs and OCR are not supported
- Unpackaged builds from this source tree are not gated by Store trial or purchase checks

---

Link tags in this file to GitHub releases when those tags are published, for example:

- `[1.0.0]: https://github.com/plainbytesstudio/PlainBytes.DocumentExtractor/releases/tag/v1.0.0.0`
