Synthetic WMF, EMF and EMF+ files recorded by Windows itself through egami-compare's `MetafileGenerator` (GDI's
CreateMetaFile and CreateEnhMetaFile, and a recording GDI+ Metafile), one family of records per file, for the Egami.Metafiles
tests and as fuzz seeds. `gdi-*.wmf` are placeable at 96 units per inch, `gdi-*.plain.wmf` have no placeable header, and each
`plus-*` scenario is written twice: `.plus.emf` (EMF+ only) and `.dual.emf` (EMF+ dual). The references (GDI, GDI+ and
LibreOffice PNGs, header facts) are in egami-compare's `references/metafiles/synthetic`; regenerate both with
`EGAMI_METAFILE_MAKE=1` there (see its README). Public domain.
