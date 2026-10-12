# License scopes

The root MIT license applies to original FullOffice application and distribution
code unless a file or component specifies otherwise. It does not relicense
third-party code or assets.

- `src/FullOffice.OfficeEngine.Native`: GNU AGPL version 3, with upstream SDK
  headers retaining their original additional notices. See its `LICENSE`,
  `NOTICE.txt`, and `third_party/provenance.json`.
- `src/FullOffice.Windows/Assets/ONLYOFFICE-logo.svg`: unmodified original
  ONLYOFFICE product logo, copyright Ascensio System SIA, CC BY-SA 4.0.
- Other bundled dependencies: the original license texts and notices in
  `licenses/` and the PDFium license directories.

The currently validated 0.2.0 candidate binary does not contain the Office editing runtime.
An app-local development runtime is a separate candidate whose corresponding
source and third-party distribution documentation must accompany its release.
