# Contributing to PlainBytes DocumentExtractor

Thanks for your interest in improving PlainBytes DocumentExtractor. This project is licensed under the **GNU General Public License v3.0 (GPLv3)**. Contributions are licensed under GPLv3 as well. You retain copyright in your contributions; submitting a contribution does not assign that copyright to PlainBytes Studio.

## How to contribute

### Bug reports

Open a GitHub issue and include, when possible:

- App version (About page) and whether you used the **Microsoft Store** build or a **self-built** unpackaged binary
- Windows version (10/11) and architecture (x64)
- Steps to reproduce
- Expected vs actual behavior
- Sample PDF characteristics (text-based vs scanned; layout notes). Do **not** attach confidential documents unless you can share a redacted sample

### Feature requests

Open an issue describing the workflow, the kind of PDFs involved, and why existing templates/extraction steps are insufficient. Keep the product scope in mind: **text-based, fixed/similar-layout PDFs** — not OCR or free-form document understanding.

### Pull requests

1. Discuss larger changes in an issue first.
2. Keep changes focused; prefer small, reviewable PRs.
3. Follow existing code style (C# / WPF, CommunityToolkit.Mvvm and WPF-UI patterns already used in the tree).
4. Do not add network calls for document extraction. Processing must remain **local**.
5. Do not commit secrets, certificates (`.pfx`), Partner Center credentials, or private Store signing material.
6. Update or add docs when behavior users rely on changes (`README.md`, `BUILDING.md`, `CHANGELOG.md` as appropriate).
7. Add an entry under the `Unreleased` section of `CHANGELOG.md` for user-visible changes.

## Development setup

See [BUILDING.md](./BUILDING.md) for prerequisites and build steps.

## Project boundaries

| In scope | Out of scope (for typical contributions) |
| --- | --- |
| Extraction UX, templates, Excel export, help copy, localization | Turning the app into a cloud service |
| Bug fixes in PDF text/table extraction for selectable-text PDFs | Full OCR / scanned-PDF pipelines |
| Build and packaging docs for open-source builds | Requiring purchase to exercise GPLv3 rights on self-built copies |

Brand names, logos, and product marks of **PlainBytes Studio** remain trademarked and are not licensed under GPLv3. Forks must not present themselves as official PlainBytes Studio / Microsoft Store releases. This does not restrict accurate statements of origin, such as naming PlainBytes DocumentExtractor when describing that a build is based on or derived from this project. See [TRADEMARKS.md](./TRADEMARKS.md).

## License and Store builds

- PlainBytes DocumentExtractor is licensed under GPLv3. Users may modify, build, and redistribute the source code under the terms of the license, including modifying or removing trial and purchase checks in their own builds.
- The official Microsoft Store version offers a 7-day free trial followed by a one-time purchase. These commercial distribution terms do not restrict the rights granted under GPLv3.

## Code of conduct (short)

Be respectful in issues and reviews. Do not submit malicious content, credentials, or others’ private data.

## Questions

- Product / website: [https://plainbytes.studio](https://plainbytes.studio)
- Store listing: [Microsoft Store](https://apps.microsoft.com/detail/9PF07HL4S766)
- Email: plainbytes.studio@gmail.com
