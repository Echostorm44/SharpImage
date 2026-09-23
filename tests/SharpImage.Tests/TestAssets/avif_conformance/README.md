# AVIF conformance assets

Reference files for `Av1ConformanceDecodeTests`, `AvifCicpTests`, `AvifMetadataTests`, `AvifTransformTests` and `AvifContainerTests`.

| Files | Origin |
|---|---|
| `libavif_*_qm_dq*.avif`, `libavif_10bit_420_*.avif`, `libavif_*_tiles*.avif`, `libaom_*.avif` | Encoded for these tests with libavif 1.4.2 / libaom defaults (quantizer matrices, delta-q, delta-lf) |
| `libavif_icc_lab_alpha.avif`, `libavif_exif_xmp.avif` | Encoded for these tests with Pillow 12.1 (libavif 1.3.0) carrying an ICC profile / Exif + XMP |
| `libavif_abc_color_irot_alpha_irot.avif`, `libavif_abc_color_irot_alpha_NOirot.avif`, `libavif_clop_irot_imor.avif`, `libavif_clap_irot_imir_non_essential.avif`, `libavif_sofa_grid1x5_420*.avif`, `libavif_draw_points_idat*.avif`, `libavif_color_grid_alpha_nogrid.avif`, `libavif_paris_icc_exif_xmp.avif` | Copied unchanged from libavif's `tests/data` (BSD 2-Clause, Copyright 2019 Joe Drago; see https://github.com/AOMediaCodec/libavif/blob/main/LICENSE) |
| `sharpimage_*.avif` | Produced by SharpImage's own AV1 encoder; golden MD5s come from ffmpeg/libdav1d; `sharpimage_8bit_420_premultiplied.avif` ('prem' alpha) is checked against libavif 1.3's RGBA output instead |
