# Bundled UI fonts

- Source Han Sans CN 2.005R: unmodified mainland Simplified Chinese static subset OTF, Regular (400) and Bold (700), from the Adobe official release. WPF family: Source Han Sans CN; localized family: 思源黑体 CN.
- Inter v4.1: unmodified static TTF, Regular (400), SemiBold (600), Bold (700), from rsms' official release. WPF family: Inter.
- Original licenses: SourceHanSans/LICENSE.txt and Inter/LICENSE.txt, both SIL OFL 1.1. Preserve them in every distribution.
- Source versions, release URLs, archive checksums, extracted font/license checksums and required glyph samples are in SOURCES.json. Adobe's archive checksum matches its published release-asset digest; Inter's archive checksum was computed from the official HTTPS asset because upstream supplied no digest.
- Assets: FAswitch.Desktop/Assets/Fonts. WPF resource references for the ChunsonSwitch assembly: pack://application:,,,/ChunsonSwitch;component/Assets/Fonts/#Inter and pack://application:,,,/ChunsonSwitch;component/Assets/Fonts/#Source Han Sans CN.
- Acquisition: scripts/Acquire-UiFonts.ps1 accepts FontDirectory, LicenseDirectory, CacheDirectory, ManifestPath, ReportPath and VerifyOnly. Existing matching assets are reused without network access. Existing mismatching assets, archives and reports are refused. The script creates new files only, selects exact archive entries, verifies hashes and reads WPF faces/glyph coverage in an isolated process. It never installs system fonts, changes font data or changes execution policy.
- Independent validation: scripts/Test-UiFonts.ps1 uses WPF GlyphTypeface and Fonts.GetFontFamilies to verify static tables, exact family/weight/style, Chinese/Latin UI sample glyphs, hashes and original licenses. Evidence: FAswitch.Windows.Checks/verification/ui-fonts.json.
- Application embedding, font fallback mapping and final UI appearance are validated separately by Desktop; file inspection alone does not prove the full UI uses these faces.
