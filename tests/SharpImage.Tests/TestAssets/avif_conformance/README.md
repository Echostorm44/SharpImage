# AVIF conformance assets

Reference files for `Av1ConformanceDecodeTests`, `AvifCicpTests`, `AvifMetadataTests`, `AvifTransformTests` and `AvifContainerTests`.

| Files | Origin |
|---|---|
| `libavif_*_qm_dq*.avif`, `libavif_10bit_420_*.avif`, `libavif_*_tiles*.avif`, `libaom_*.avif` | Encoded for these tests with libavif 1.4.2 / libaom defaults (quantizer matrices, delta-q, delta-lf) |
| `libavif_icc_lab_alpha.avif`, `libavif_exif_xmp.avif` | Encoded for these tests with Pillow 12.1 (libavif 1.3.0) carrying an ICC profile / Exif + XMP |
| `libavif_abc_color_irot_alpha_irot.avif`, `libavif_abc_color_irot_alpha_NOirot.avif`, `libavif_clop_irot_imor.avif`, `libavif_clap_irot_imir_non_essential.avif`, `libavif_sofa_grid1x5_420*.avif`, `libavif_draw_points_idat*.avif`, `libavif_color_grid_alpha_nogrid.avif`, `libavif_paris_icc_exif_xmp.avif` | Copied unchanged from libavif's `tests/data` (BSD 2-Clause, Copyright 2019 Joe Drago; see https://github.com/AOMediaCodec/libavif/blob/main/LICENSE) |
| `libavif_prog_*.avif`, `libavif_layered3_8_444.avif` | Encoded for these tests with avifenc 1.4.2 (aom 3.14.1) `--progressive` / `--layered` (`--scaling-mode` 1/4, 1/2, 1/1); references from avifdec `--progressive --index` and dav1d `--alllayers 0` |
| `libaom_fg_*.avif` | Encoded for these tests with ffmpeg (2025-05 build) + libaom (`-aom-params film-grain-test=N` test vectors, or `-denoise-noise-level` estimated grain); golden MD5s from libdav1d with and without grain |
| `sharpimage_*.avif` | Produced by SharpImage's own AV1 encoder; golden MD5s come from ffmpeg/libdav1d; `sharpimage_8bit_420_premultiplied.avif` ('prem' alpha) is checked against libavif 1.3's RGBA output instead; `sharpimage_fg_*` carry film grain (libaom test vectors) and are checked against libdav1d with and without grain |
