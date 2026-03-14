// SharpImage CLI — Entry point.
// Pure C# .NET 10 image processing — 27 formats, zero native dependencies.

using SharpImage.Cli;
using System.CommandLine;
using System.CommandLine.Parsing;

var rootCommand = new RootCommand("""
    SharpImage — Pure C# .NET 10 Image Processing CLI
    
    Supports 33 image formats with zero native dependencies.
    Full AOT-compatible. No C/C++ libraries required.
    
    Format Support:
      Raster: PNG, JPEG, GIF, BMP, TGA, PNM, TIFF, WebP, QOI, ICO, PCX, WBMP,
              Farbfeld, XBM, XPM, DDS, PSD, HDR, DPX, FITS, CIN
      Medical: DICOM
      Modern:  AVIF, HEIC, JPEG 2000, JPEG XL
      HDR/VFX: OpenEXR
      Vector:  SVG (rasterized)
    
    Operations:
      Transform: convert, progressive, resize, crop, rotate, flip, flop, distort, trim,
                 extent, shave, chop, transpose, transverse, deskew, topdf, shear,
                 autoorient, roll, splice, cafix, vigcorrect, perspcorrect
      Effects:   blur, sharpen, edge, emboss, border, frame, raise, shade, blueshift,
                 shadow, polaroid
      Color:     brightness, contrast, gamma, grayscale, invert, threshold,
                 posterize, saturate, levels, opaquepaint, transparentpaint, tint
      Enhance:   equalize, normalize, autolevel, autogamma, sigmoidal, clahe,
                 modulate, whitebalance, colorize, solarize, sepia, curves,
                 dodge, burn, exposure, vibrance, dehaze
      Blur/Noise: motionblur, radialblur, selectiveblur, noise, despeckle,
                  waveletdenoise
      Artistic:  oilpaint, charcoal, sketch, vignette, wave, swirl, implode
      Advanced:  morphology, quantize, dither, composite, compare, fourier, draw,
                 segment, sparsecolor, stegano, inpaint, clonestamp, healingbrush, redeye
      Selection: floodselect, colorselect, grabcut, feather, maskop, applymask
      Stitching: stitch, lapblend
      Smart Removal: saliency, bgremove, cafill, objremove
      Color Grading: colortransfer, splittone, gradmap, channelmix, photofilter, duotone
      Advanced Transform: liquify, freqsep, focusstack
      HDR: hdrmerge, tonemap-reinhard, tonemap-drago, expfusion
      Creative: lensblur, tiltshift, glow, pixelate, crystallize, pointillize, halftone
      Displacement: displace, normalmap, spherize
      Accessibility: colorblindsim, daltonize, softproof
      Texture: seamlesstile, texsynth
      Game Dev: spritesheet, cubemapextract, cubemapstitch
      Analysis:  phash, canny, houghlines, connectedcomponents, meanshift
      FX:        fx (per-pixel math expression evaluator)
      Channel:   separate, combine, swapchannel
      Metadata:  exif, iccprofile, metadata
      Animation: gifanim, gifsplit, gifinfo, apnganim, apngsplit, apnginfo
      Montage:   append, montage, coalesce
      Colorspace: colorspace, colorspace-roundtrip, colorspaces
      Novel:     seamcarve, energymap, hashsuite, histmatch, histogram, smartcrop, interestmap, pipeline, tensor, palette, diff, animwebp, moderndither
      Pixel:     sortpixels, uniquecolors, clamp, cycle, strip, colorthreshold, randomthreshold, levelcolors, modefilter
      Info:      info, formats, psdlayers
    
    Examples:
      sharpimage convert photo.jpg photo.png
      sharpimage resize photo.jpg -w 800 -h 600 resized.png
      sharpimage blur photo.jpg --sigma 3.0 blurred.png
      sharpimage compare original.png modified.png --diff difference.png
      sharpimage info photo.jpg --stats
      sharpimage formats
    """);

// Transform commands
rootCommand.Add(TransformCommands.CreateConvertCommand());
rootCommand.Add(TransformCommands.CreateProgressiveJpegCommand());
rootCommand.Add(TransformCommands.CreateResizeCommand());
rootCommand.Add(TransformCommands.CreateCropCommand());
rootCommand.Add(TransformCommands.CreateRotateCommand());
rootCommand.Add(TransformCommands.CreateFlipCommand());
rootCommand.Add(TransformCommands.CreateFlopCommand());
rootCommand.Add(TransformCommands.CreateDistortCommand());
rootCommand.Add(TransformCommands.CreateAffineCommand());
rootCommand.Add(TransformCommands.CreateTrimCommand());
rootCommand.Add(TransformCommands.CreateExtentCommand());
rootCommand.Add(TransformCommands.CreateShaveCommand());
rootCommand.Add(TransformCommands.CreateChopCommand());
rootCommand.Add(TransformCommands.CreateTransposeCommand());
rootCommand.Add(TransformCommands.CreateTransverseCommand());
rootCommand.Add(TransformCommands.CreateDeskewCommand());
rootCommand.Add(TransformCommands.CreatePdfExportCommand());
rootCommand.Add(TransformCommands.CreateChromaticAberrationCommand());
rootCommand.Add(TransformCommands.CreateVignetteCorrectionCommand());
rootCommand.Add(TransformCommands.CreatePerspectiveCorrectionCommand());

// Effects commands
rootCommand.Add(EffectsCommands.CreateBlurCommand());
rootCommand.Add(EffectsCommands.CreateSharpenCommand());
rootCommand.Add(EffectsCommands.CreateUnsharpMaskCommand());
rootCommand.Add(EffectsCommands.CreateEdgeCommand());
rootCommand.Add(EffectsCommands.CreateEmbossCommand());

// Color adjustment commands
rootCommand.Add(EffectsCommands.CreateBrightnessCommand());
rootCommand.Add(EffectsCommands.CreateContrastCommand());
rootCommand.Add(EffectsCommands.CreateGammaCommand());
rootCommand.Add(EffectsCommands.CreateGrayscaleCommand());
rootCommand.Add(EffectsCommands.CreateInvertCommand());
rootCommand.Add(EffectsCommands.CreateThresholdCommand());
rootCommand.Add(EffectsCommands.CreatePosterizeCommand());
rootCommand.Add(EffectsCommands.CreateSaturateCommand());
rootCommand.Add(EffectsCommands.CreateLevelsCommand());
rootCommand.Add(EffectsCommands.CreateColorMatrixCommand());

// Decorative & paint commands
rootCommand.Add(EffectsCommands.CreateBorderCommand());
rootCommand.Add(EffectsCommands.CreateFrameCommand());
rootCommand.Add(EffectsCommands.CreateRaiseCommand());
rootCommand.Add(EffectsCommands.CreateShadeCommand());
rootCommand.Add(EffectsCommands.CreateOpaquePaintCommand());
rootCommand.Add(EffectsCommands.CreateTransparentPaintCommand());
rootCommand.Add(EffectsCommands.CreateShearCommand());

// Bundle G commands
rootCommand.Add(EffectsCommands.CreateAutoOrientCommand());
rootCommand.Add(EffectsCommands.CreateRollCommand());
rootCommand.Add(EffectsCommands.CreateSpliceCommand());
rootCommand.Add(EffectsCommands.CreateBlueShiftCommand());
rootCommand.Add(EffectsCommands.CreateTintCommand());
rootCommand.Add(EffectsCommands.CreateShadowCommand());
rootCommand.Add(EffectsCommands.CreateSteganoCommand());
rootCommand.Add(EffectsCommands.CreatePolaroidCommand());
rootCommand.Add(EffectsCommands.CreateSegmentCommand());
rootCommand.Add(EffectsCommands.CreateSparseColorCommand());

// Advanced commands
rootCommand.Add(AdvancedCommands.CreateMorphologyCommand());
rootCommand.Add(AdvancedCommands.CreateQuantizeCommand());
rootCommand.Add(AdvancedCommands.CreateDitherCommand());
rootCommand.Add(AdvancedCommands.CreateCompositeCommand());
rootCommand.Add(AdvancedCommands.CreateCompareCommand());
rootCommand.Add(AdvancedCommands.CreateFourierCommand());
rootCommand.Add(AdvancedCommands.CreateDrawCommand());

// Retouching commands (Bundle A)
rootCommand.Add(AdvancedCommands.CreateInpaintCommand());
rootCommand.Add(AdvancedCommands.CreateCloneStampCommand());
rootCommand.Add(AdvancedCommands.CreateHealingBrushCommand());
rootCommand.Add(AdvancedCommands.CreateRedEyeCommand());

// Selection & masking commands
rootCommand.Add(AdvancedCommands.CreateFloodSelectCommand());
rootCommand.Add(AdvancedCommands.CreateColorSelectCommand());
rootCommand.Add(AdvancedCommands.CreateGrabCutCommand());
rootCommand.Add(AdvancedCommands.CreateFeatherCommand());
rootCommand.Add(AdvancedCommands.CreateMaskOpCommand());
rootCommand.Add(AdvancedCommands.CreateApplyMaskCommand());
rootCommand.Add(AdvancedCommands.CreateAlphaMattingCommand());

// Stitching commands
rootCommand.Add(AdvancedCommands.CreateStitchCommand());
rootCommand.Add(AdvancedCommands.CreateLaplacianBlendCommand());

// Smart removal commands
rootCommand.Add(AdvancedCommands.CreateSaliencyMapCommand());
rootCommand.Add(AdvancedCommands.CreateAutoBackgroundRemoveCommand());
rootCommand.Add(AdvancedCommands.CreateContentAwareFillCommand());
rootCommand.Add(AdvancedCommands.CreateObjectRemoveCommand());

// Color grading commands
rootCommand.Add(EnhanceCommands.CreateColorTransferCommand());
rootCommand.Add(EnhanceCommands.CreateSplitToningCommand());
rootCommand.Add(EnhanceCommands.CreateGradientMapCommand());
rootCommand.Add(EnhanceCommands.CreateChannelMixerCommand());
rootCommand.Add(EnhanceCommands.CreatePhotoFilterCommand());
rootCommand.Add(EnhanceCommands.CreateDuotoneCommand());

// Advanced transform commands
rootCommand.Add(TransformCommands.CreateLiquifyCommand());
rootCommand.Add(TransformCommands.CreateFreqSepCommand());
rootCommand.Add(TransformCommands.CreateFocusStackCommand());

// HDR commands
rootCommand.Add(EffectsCommands.CreateHdrMergeCommand());
rootCommand.Add(EffectsCommands.CreateToneMapReinhardCommand());
rootCommand.Add(EffectsCommands.CreateToneMapDragoCommand());
rootCommand.Add(EffectsCommands.CreateExposureFusionCommand());

// Creative Filters II commands
rootCommand.Add(EffectsCommands.CreateLensBlurCommand());
rootCommand.Add(EffectsCommands.CreateTiltShiftCommand());
rootCommand.Add(EffectsCommands.CreateGlowCommand());
rootCommand.Add(EffectsCommands.CreatePixelateCommand());
rootCommand.Add(EffectsCommands.CreateCrystallizeCommand());
rootCommand.Add(EffectsCommands.CreatePointillizeCommand());
rootCommand.Add(EffectsCommands.CreateHalftoneCommand());

// Morph, Anaglyph, Enhance, ChromaKey, ChannelFx, CDL
rootCommand.Add(EffectsCommands.CreateMorphCommand());
rootCommand.Add(EffectsCommands.CreateAnaglyphCommand());
rootCommand.Add(EffectsCommands.CreateEnhanceCommand());
rootCommand.Add(EffectsCommands.CreateChromaKeyCommand());
rootCommand.Add(EffectsCommands.CreateChannelFxCommand());
rootCommand.Add(EffectsCommands.CreateCdlCommand());

// Phase 47: Utility operations
rootCommand.Add(TransformCommands.CreateThumbnailCommand());
rootCommand.Add(TransformCommands.CreateResampleCommand());
rootCommand.Add(TransformCommands.CreateMagnifyCommand());
rootCommand.Add(TransformCommands.CreateMinifyCommand());
rootCommand.Add(TransformCommands.CreateCropToTilesCommand());
rootCommand.Add(TransformCommands.CreateSampleCommand());
rootCommand.Add(TransformCommands.CreateAdaptiveResizeCommand());
rootCommand.Add(EffectsCommands.CreateTextureImageCommand());
rootCommand.Add(EffectsCommands.CreateRemapImageCommand());
rootCommand.Add(EffectsCommands.CreateRangeThresholdCommand());
rootCommand.Add(EffectsCommands.CreateDistanceTransformCommand());

// Phase 49: Additional operations
rootCommand.Add(EffectsCommands.CreateColorThresholdCommand());
rootCommand.Add(EffectsCommands.CreateRandomThresholdCommand());
rootCommand.Add(EffectsCommands.CreateSortPixelsCommand());
rootCommand.Add(EffectsCommands.CreateUniqueColorsCommand());
rootCommand.Add(EffectsCommands.CreateClampCommand());
rootCommand.Add(EffectsCommands.CreateCycleColormapCommand());
rootCommand.Add(EffectsCommands.CreateStripCommand());
rootCommand.Add(BlurNoiseCommands.CreateModeFilterCommand());
rootCommand.Add(EnhanceCommands.CreateLevelColorsCommand());

// Alpha operations
rootCommand.Add(ChannelCommands.CreateAlphaExtractCommand());
rootCommand.Add(ChannelCommands.CreateAlphaRemoveCommand());
rootCommand.Add(ChannelCommands.CreateAlphaSetCommand());
rootCommand.Add(ChannelCommands.CreateAlphaOpaqueCommand());
rootCommand.Add(ChannelCommands.CreateAlphaTransparentCommand());

// Displacement & Maps commands
rootCommand.Add(AdvancedCommands.CreateDisplacementMapCommand());
rootCommand.Add(AdvancedCommands.CreateNormalMapCommand());
rootCommand.Add(AdvancedCommands.CreateSpherizeCommand());

// Accessibility & Print commands
rootCommand.Add(EnhanceCommands.CreateColorBlindSimCommand());
rootCommand.Add(EnhanceCommands.CreateDaltonizeCommand());
rootCommand.Add(EnhanceCommands.CreateSoftProofCommand());

// Texture Tools commands
rootCommand.Add(AdvancedCommands.CreateSeamlessTileCommand());
rootCommand.Add(AdvancedCommands.CreateTexSynthCommand());

// Game Dev commands
rootCommand.Add(AdvancedCommands.CreateSpriteSheetCommand());
rootCommand.Add(AdvancedCommands.CreateCubemapExtractCommand());
rootCommand.Add(AdvancedCommands.CreateCubemapStitchCommand());

// Enhancement commands
rootCommand.Add(EnhanceCommands.CreateEqualizeCommand());
rootCommand.Add(EnhanceCommands.CreateNormalizeCommand());
rootCommand.Add(EnhanceCommands.CreateAutoLevelCommand());
rootCommand.Add(EnhanceCommands.CreateAutoGammaCommand());
rootCommand.Add(EnhanceCommands.CreateSigmoidalContrastCommand());
rootCommand.Add(EnhanceCommands.CreateClaheCommand());
rootCommand.Add(EnhanceCommands.CreateModulateCommand());
rootCommand.Add(EnhanceCommands.CreateWhiteBalanceCommand());
rootCommand.Add(EnhanceCommands.CreateColorizeCommand());
rootCommand.Add(EnhanceCommands.CreateSolarizeCommand());
rootCommand.Add(EnhanceCommands.CreateSepiaCommand());
rootCommand.Add(EnhanceCommands.CreateLocalContrastCommand());
rootCommand.Add(EnhanceCommands.CreateClutCommand());
rootCommand.Add(EnhanceCommands.CreateHaldClutCommand());
rootCommand.Add(EnhanceCommands.CreateLevelizeCommand());
rootCommand.Add(EnhanceCommands.CreateContrastStretchCommand());
rootCommand.Add(EnhanceCommands.CreateLinearStretchCommand());
rootCommand.Add(EnhanceCommands.CreateCurvesCommand());
rootCommand.Add(EnhanceCommands.CreateDodgeCommand());
rootCommand.Add(EnhanceCommands.CreateBurnCommand());
rootCommand.Add(EnhanceCommands.CreateExposureCommand());
rootCommand.Add(EnhanceCommands.CreateVibranceCommand());
rootCommand.Add(EnhanceCommands.CreateDehazeCommand());

// Blur and noise commands
rootCommand.Add(BlurNoiseCommands.CreateMotionBlurCommand());
rootCommand.Add(BlurNoiseCommands.CreateRadialBlurCommand());
rootCommand.Add(BlurNoiseCommands.CreateSelectiveBlurCommand());
rootCommand.Add(BlurNoiseCommands.CreateAddNoiseCommand());
rootCommand.Add(BlurNoiseCommands.CreateDespeckleCommand());
rootCommand.Add(BlurNoiseCommands.CreateWaveletDenoiseCommand());
rootCommand.Add(BlurNoiseCommands.CreateSpreadCommand());
rootCommand.Add(BlurNoiseCommands.CreateMedianCommand());
rootCommand.Add(BlurNoiseCommands.CreateBilateralCommand());
rootCommand.Add(BlurNoiseCommands.CreateKuwaharaCommand());
rootCommand.Add(BlurNoiseCommands.CreateAdaptiveBlurCommand());
rootCommand.Add(BlurNoiseCommands.CreateAdaptiveSharpenCommand());

// Artistic effect commands
rootCommand.Add(ArtisticCommands.CreateOilPaintCommand());
rootCommand.Add(ArtisticCommands.CreateCharcoalCommand());
rootCommand.Add(ArtisticCommands.CreateSketchCommand());
rootCommand.Add(ArtisticCommands.CreateVignetteCommand());
rootCommand.Add(ArtisticCommands.CreateWaveCommand());
rootCommand.Add(ArtisticCommands.CreateSwirlCommand());
rootCommand.Add(ArtisticCommands.CreateImplodeCommand());

// Info commands
rootCommand.Add(InfoCommands.CreateInfoCommand());
rootCommand.Add(InfoCommands.CreateFormatsCommand());
rootCommand.Add(InfoCommands.CreatePsdLayersCommand());

// Analysis & detection commands
rootCommand.Add(AnalysisCommands.CreatePhashCommand());
rootCommand.Add(AnalysisCommands.CreateCannyCommand());
rootCommand.Add(AnalysisCommands.CreateHoughCommand());
rootCommand.Add(AnalysisCommands.CreateConnectedComponentsCommand());
rootCommand.Add(AnalysisCommands.CreateMeanShiftCommand());

// Math operations (evaluate, statistic, function, polynomial)
rootCommand.Add(MathCommands.CreateEvaluateCommand());
rootCommand.Add(MathCommands.CreateStatisticCommand());
rootCommand.Add(MathCommands.CreateFunctionCommand());
rootCommand.Add(MathCommands.CreatePolynomialCommand());

// FX expression evaluator
rootCommand.Add(FxCommands.CreateFxCommand());

// Channel commands
rootCommand.Add(ChannelCommands.CreateSeparateCommand());
rootCommand.Add(ChannelCommands.CreateCombineCommand());
rootCommand.Add(ChannelCommands.CreateSwapCommand());

// Metadata commands
rootCommand.Add(MetadataCommands.CreateExifCommand());
rootCommand.Add(MetadataCommands.CreateIccProfileCommand());
rootCommand.Add(MetadataCommands.CreateMetadataCommand());

// Animation commands
rootCommand.Add(AnimationCommands.CreateGifAnimCommand());
rootCommand.Add(AnimationCommands.CreateGifSplitCommand());
rootCommand.Add(AnimationCommands.CreateGifInfoCommand());
rootCommand.Add(AnimationCommands.CreateApngAnimCommand());
rootCommand.Add(AnimationCommands.CreateApngSplitCommand());
rootCommand.Add(AnimationCommands.CreateApngInfoCommand());

// Montage commands
rootCommand.Add(MontageCommands.CreateAppendCommand());
rootCommand.Add(MontageCommands.CreateMontageCommand());
rootCommand.Add(MontageCommands.CreateCoalesceCommand());

// Colorspace commands
rootCommand.Add(ColorspaceCommands.CreateColorspaceConvertCommand());
rootCommand.Add(ColorspaceCommands.CreateColorspaceRoundtripCommand());
rootCommand.Add(ColorspaceCommands.CreateListColorspacesCommand());

// Novel feature commands
rootCommand.Add(NovelCommands.CreateSeamCarveCommand());
rootCommand.Add(NovelCommands.CreateEnergyMapCommand());
rootCommand.Add(NovelCommands.CreateHashSuiteCommand());
rootCommand.Add(NovelCommands.CreateHistogramMatchCommand());
rootCommand.Add(NovelCommands.CreateHistogramRenderCommand());
rootCommand.Add(NovelCommands.CreateSmartCropCommand());
rootCommand.Add(NovelCommands.CreateInterestMapCommand());
rootCommand.Add(NovelCommands.CreatePipelineCommand());
rootCommand.Add(NovelCommands.CreateTensorExportCommand());
rootCommand.Add(NovelCommands.CreatePaletteCommand());
rootCommand.Add(NovelCommands.CreateDiffCommand());
rootCommand.Add(NovelCommands.CreateAnimWebpCommand());
rootCommand.Add(NovelCommands.CreateModernDitherCommand());

// Image generation commands
rootCommand.Add(GenerateCommands.CreateGradientCommand());
rootCommand.Add(GenerateCommands.CreatePlasmaCommand());
rootCommand.Add(GenerateCommands.CreatePatternCommand());
rootCommand.Add(GenerateCommands.CreateLabelCommand());
rootCommand.Add(GenerateCommands.CreateSolidCommand());
rootCommand.Add(GenerateCommands.CreateNoiseCommand());

// Show banner when invoked without arguments
rootCommand.SetAction((_) =>
{
    CliOutput.PrintBanner();
    Spectre.Console.AnsiConsole.MarkupLine("[dim]Run [bold]sharpimage --help[/] for usage information.[/]");
});

return CommandLineParser.Parse(rootCommand, args).Invoke();
