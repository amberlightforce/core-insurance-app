# F-1f(a) — Gotenberg PDF/A spike: findings

Date: 2026-10-07. Scope: PRD-10 REQ-DOC-149, -150, -157, -160, -161, -167, -170, -291 against the infra mandate "HTML → PDF/A via Gotenberg (headless Chromium)" and D-ARC-07 (in-house composer → Gotenberg paginates → .NET post-processing).

## Summary

| # | Requirement | Feasible? | One-line answer |
|---|---|---|---|
| 1 | PDF/A-3 **level a** + embedded payload (REQ-DOC-157) | **Yes, but not through Gotenberg's PDF/A option** | Gotenberg 8 offers only PDF/A-1b/2b/3b, produced by LibreOffice. Chromium's tagged PDF plus a small .NET finaliser (no third-party PDF library) **passed veraPDF PDF/A-3a, 3b and 3u**; the embedded payload.json hash matched the stored payload hash. |
| 2 | Tagged / PDF/UA-1 (REQ-DOC-291) | **Yes (machine checks)** | Chromium `generateTaggedPDF` output fails only two checks: untagged page furniture and no XMP. The finaliser fixes both, and the output **passes veraPDF PDF/UA-1**. Matterhorn manual checks not done. |
| 3 | Byte-reproducible re-render (REQ-DOC-160) | **Yes, after normalisation** | Raw output is never byte-identical (dates, sometimes StructElem /IDs). After the finaliser, SHA-256 was **identical across 9 renders in 4 sessions**. Holds only within one pinned engine image. |
| 4 | Fail on missing glyph (REQ-DOC-149) | **Yes, with our own checks only** | Chromium falls back silently to system fonts and veraPDF **passes** such files. A pre-render cmap gate (names the code point) and a post-render font gate both worked. Noto Sans and Inter cover all 233 Greek Extended (polytonic) code points. |
| 5 | Seal after PDF/A, PAdES + LTV (REQ-DOC-167) | **B-B proven; LT/LTA feasible, not built** | Invisible PAdES B-B seal as an incremental update with in-box .NET crypto. The CMS validates, the file **still passes PDF/A-3a and UA-1**, and the pre-seal bytes are an exact prefix. |
| 6 | 500+ pages, bounded memory (REQ-DOC-170, NFR-DOC-002) | **Partial** | 601 pages in 36.7 s, but **Chromium peaked at 5.6 GiB** (about 9 MiB per page, linear). Needs chunks of about 150 pages or fewer plus a merge (page offsets proven; tagged merge not built). Finaliser peak 361 MiB. |

**Headline for D-ARC-07:** keep Gotenberg as the paginator, using only its Chromium route with `generateTaggedPdf=true`. **Do not use** Gotenberg's `pdfa`, `pdfua`, `metadata` or `embeds` options for the archive rendition. PDF/A-3a, PDF/UA, payload embedding, deterministic metadata, sealing and the glyph gates belong in our .NET finaliser (about 450 lines, no PDF library).

## Environment and what was actually run

- **Docker could not be used.** Docker Desktop 4.66.1 failed at start-up on stale AF_UNIX socket reparse points dated 2026-10-02 (`%LOCALAPPDATA%\Docker\run\dockerInference`, then `%LOCALAPPDATA%\docker-secrets-engine\engine.sock`). This needs a user fix.
- Evidence was produced with the same Chromium call Gotenberg makes: local Chrome **154.0.8037.98** headless over CDP, `Page.printToPDF` with Gotenberg's Chromium-module parameters (`generateTaggedPDF`, `generateDocumentOutline`, `preferCSSPageSize`, `printBackground`, paper and margins). .NET SDK 10.0.401 and veraPDF **1.30.2** (Temurin JRE 21) were installed in a scratch folder. Host: Windows 11 26200.
- The Gotenberg path is implemented but **not executed** (`GotenbergRenderer`, `--mode=gotenberg-native`, `docker-compose.yml` with `gotenberg/gotenberg:8.37.0` and `verapdf/cli:v1.30.2`, `run-docker.sh`). Statements about Gotenberg's own PDF/A, PDF/UA, metadata and embeds behaviour come from its documentation and defaults, not measurement.
- **Re-run:** `run-local.ps1 -Dotnet <dotnet> -VeraPdf <verapdf.bat>`, or `run-docker.sh`. The Docker script also runs the native variants and measures memory with `mem_limit: 2g` and `cpus: 1`.
- **Evidence:** `evidence/` holds results-*.txt, veraPDF summaries and failures, and sample PDFs (raw, pdfa, sealed, glyph fallback, header/footer fallback).

Test document (`Composer.cs`): a Greek motor policy schedule with `lang="el"`, an all-caps title without tonos, «», final sigma, dialytika, a polytonic sentence, el-GR amounts (`28.028,78 €`), a cover table spanning pages with a repeating `thead`, and a "Σελίδα X από Y" footer as CSS `@page` margin boxes. Fonts: Noto Sans 2.015, self-hosted.

## 1. PDF/A-3a + payload
- **Gotenberg (from documentation):** `pdfa` accepts only 1b/2b/3b via LibreOffice; no level a or u. Embeds use pdfcpu and metadata uses ExifTool. Whether tags or conformance survive these steps is untested.
- **Measured:** the finalised files (5-page, sealed, and 601-page) pass 3a, 3b, 3u and UA-1. Raw Chromium fails 3b on 6.2.4.3 (DeviceRGB without OutputIntent), 6.1.3 (no trailer /ID) and 6.6.2.1 (no XMP).
- **The finaliser:**
  - Rewrites the file from Skia's classic xref.
  - Adds XMP: `pdfaid` 3/A, `pdfuaid` 1, and an extension schema `doc:` holding DocumentNumber, DocumentType, TemplateVersion, PayloadSha256 and Language.
  - Adds an sRGB OutputIntent.
  - Embeds the payload as an associated file (`/AF`, `/AFRelationship /Data`, `application/json`, Params) and lists it in `/Names/EmbeddedFiles`.
- **Design:** call Gotenberg's Chromium route with `generateTaggedPdf`, `failOnConsoleExceptions`, `failOnResourceLoadingFailed` and `preferCssPageSize`. Then run the .NET ArchiveFinaliser and validate with veraPDF in CI. Ship the official ICC sRGB profile; the spike generates an approximate one.
- **Risks:** the finaliser depends on Skia's output shape, so Chromium upgrades need golden tests. Tag quality is Chromium's.

## 2. PDF/UA
- Raw Chromium fails only 7.1-3 (CSS margin-box text emitted outside marked content) and 7.1-8 (no XMP). Tables have TH elements with Scope; headings, lists and paragraphs are tagged.
- **Fix:** wrap painting runs at marked-content depth 0 in `/Artifact BMC…EMC`.
- Do **not** use Chromium header/footer templates. Their text is untagged and their web fonts are ignored (Arial fallback). Use CSS `@page` margin boxes instead.
- **Risks:** artifacting must be limited to the margin area, and untagged body content must fail. Matterhorn and PDF/UA-2 not tested.

## 3. Determinism
- **Raw output varies:** Info dates always differ; StructElem `/ID (nodeNNNN)` and IDTree order sometimes differ; there is no trailer /ID; `/Creator` contains the user agent and host OS.
- **Finaliser normalisation:** dates come from the payload reference time; `/ID` = SHA-256("DOC-ID:"+document number)[0..16]; Info is reduced; struct IDs are renumbered; streams are re-deflated.
- **Result:** sha256 `2e69cc4b…a9fc` across 9 renders in 4 sessions. The sealed file's pre-seal prefix has the same hash, so reproducibility checks compare `sealed[0..firstRevisionLength]`.
- **Engine version (REQ-DOC-161)** = Gotenberg image digest + finaliser assembly + .NET runtime + font-set hashes + ICC profile. Keep all of these for the full retention period.
- **Untested:** cross-host, and the Linux container versus Windows.

## 4. Missing glyph
- Silent fallbacks to MicrosoftYaHei (CJK), a Type3 emoji font and SegoeUISymbol (Coptic) all **pass** veraPDF. Only `.notdef` cases fail.
- **Three layers:**
  1. Pre-render cmap gate: `DOC-ERR-GLYPH-MISSING U+XXXX`, archive nothing.
  2. A Gotenberg image containing only the declared fonts.
  3. A post-render PdfPig gate: every embedded font must be in the declared set, with no `.notdef`.

## 5. Seal
- **Built:** an incremental update adding AcroForm (SigFlags 3), an invisible widget, and `/Sig` with ETSI.CAdES.detached. The CMS is SHA-256 / RSA-3072 with ESS signing-certificate-v2 (B-B), using a self-signed test certificate.
- **Not built:** B-T (RFC 3161 is in-box), B-LT (DSS with OCSP/CRL; BouncyCastle.Cryptography, MIT), B-LTA, EU DSS validation (LGPL-2.1 sidecar), and real keys (Key Vault Premium / Managed HSM or QTSP CSC).
- **Options:** in-box .NET + BouncyCastle (recommended); EU DSS for validation; PDFsharp 6.2+ (MIT, not evaluated). iText is excluded (AGPL); QuestPDF is revenue-gated and cannot sign.

## 6. Large documents

| Pages | Render | Chromium peak | PDF size | Finaliser | .NET peak |
|---|---|---|---|---|---|
| 142 | 6.4 s | 1,229 MiB | 7.0 MiB | 1.6 s | 153 MiB |
| 284 | 13.3 s | 2,212 MiB | 14.1 MiB | 2.8 s | 246 MiB |
| 601 | 36.7 s | 5,598 MiB | 28.5 MiB | 4.8 s | 361 MiB |
| 601 untagged | 16.6 s | 5,383 MiB | 2.4 MiB | — | — |

- **Design:** chunks of about 150 pages or fewer; page offsets via `html { counter-reset: page N }` (proven); total pages from a first pass. The merge in the finaliser (page trees, StructTreeRoot, ParentTree) is **not built**.
- **Alternative:** a separate large-document Gotenberg pool with 6–8 GiB.

## Dependencies
Gotenberg 8.37.0 (MIT, Chromium route only), PdfPig 0.1.11 (Apache-2.0), System.Security.Cryptography.Pkcs 10.0.0 (MIT), BouncyCastle.Cryptography (MIT, proposed), EU DSS (LGPL-2.1 sidecar, proposed), Noto Sans 2.015 / Inter 4.001 (OFL-1.1), the official ICC sRGB profile, and veraPDF 1.30.2 (CI only). Excluded: iText.

## Not tested
1. Gotenberg itself: native options, memory at 2 GiB, 1 vCPU timing, Linux determinism.
2. Cross-host reproducibility.
3. PAdES B-T/LT/LTA, real keys, DSS validation.
4. The chunk merge.
5. Matterhorn, PDF/UA-2, screen readers.
