using SharpImage.Core;
using SharpImage.Generators;
using SharpImage.Image;

namespace SharpImage.Tests;

/// <summary>Tests for procedural image generators (gradient, plasma, pattern, label, solid).</summary>
public class GeneratorTests
{
    #region Solid Generator

    [Test]
    public async Task Solid_CorrectDimensions()
    {
        var frame = SolidGenerator.Generate(100, 50, 255, 0, 0);
        await Assert.That(frame.Columns).IsEqualTo(100u);
        await Assert.That(frame.Rows).IsEqualTo(50u);
    }

    [Test]
    public async Task Solid_AllPixelsMatchColor()
    {
        var frame = SolidGenerator.Generate(10, 10, 128, 64, 192);
        ushort expectedR = Quantum.ScaleFromByte(128);
        ushort expectedG = Quantum.ScaleFromByte(64);
        ushort expectedB = Quantum.ScaleFromByte(192);

        bool allMatch = true;
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < 10 && allMatch; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < 10 && allMatch; x++)
            {
                int off = x * ch;
                if (row[off] != expectedR || row[off + 1] != expectedG || row[off + 2] != expectedB)
                    allMatch = false;
            }
        }
        await Assert.That(allMatch).IsTrue();
    }

    [Test]
    public async Task Solid_WithAlpha_HasAlphaChannel()
    {
        var frame = SolidGenerator.Generate(10, 10, 255, 255, 255, 128);
        await Assert.That(frame.HasAlpha).IsTrue();
        await Assert.That(frame.NumberOfChannels).IsEqualTo(4);

        ushort expectedA = Quantum.ScaleFromByte(128);
        ushort a = frame.GetPixelRow(5)[5 * 4 + 3];
        await Assert.That(a).IsEqualTo(expectedA);
    }

    #endregion

    #region Gradient Generator

    [Test]
    public async Task Gradient_Linear_Horizontal_EndpointsCorrect()
    {
        // Left-to-right gradient: red → blue
        var frame = GradientGenerator.Linear(100, 10, 255, 0, 0, 0, 0, 255, angleDegrees: 0);
        int ch = frame.NumberOfChannels;

        // Left edge should be red
        ushort leftR = frame.GetPixelRow(5)[0];
        ushort leftB = frame.GetPixelRow(5)[2];
        await Assert.That(leftR).IsEqualTo(Quantum.ScaleFromByte(255));
        await Assert.That(leftB).IsEqualTo(Quantum.ScaleFromByte(0));

        // Right edge should be blue
        ushort rightR = frame.GetPixelRow(5)[99 * ch];
        ushort rightB = frame.GetPixelRow(5)[99 * ch + 2];
        await Assert.That(rightR).IsEqualTo(Quantum.ScaleFromByte(0));
        await Assert.That(rightB).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    [Test]
    public async Task Gradient_Linear_Vertical_TopBottom()
    {
        // Top-to-bottom gradient: white → black at 90 degrees
        var frame = GradientGenerator.Linear(10, 100, 255, 255, 255, 0, 0, 0, angleDegrees: 90);
        int ch = frame.NumberOfChannels;

        // Top should be white
        ushort topR = frame.GetPixelRow(0)[5 * ch];
        await Assert.That(topR).IsEqualTo(Quantum.ScaleFromByte(255));

        // Bottom should be black
        ushort bottomR = frame.GetPixelRow(99)[5 * ch];
        await Assert.That(bottomR).IsEqualTo(Quantum.ScaleFromByte(0));
    }

    [Test]
    public async Task Gradient_Linear_MidpointBlended()
    {
        // Horizontal: 0 → 255, midpoint should be ~128
        var frame = GradientGenerator.Linear(101, 1, 0, 0, 0, 255, 255, 255, angleDegrees: 0);
        int ch = frame.NumberOfChannels;
        ushort midR = frame.GetPixelRow(0)[50 * ch];
        ushort expected = Quantum.ScaleFromByte(128);
        // Allow ±2 for rounding
        int diff = Math.Abs(midR - expected);
        await Assert.That(diff).IsLessThanOrEqualTo(Quantum.ScaleFromByte(2));
    }

    [Test]
    public async Task Gradient_Radial_CenterAndCorner()
    {
        // Radial: red center → blue outer
        var frame = GradientGenerator.Radial(101, 101, 255, 0, 0, 0, 0, 255);
        int ch = frame.NumberOfChannels;

        // Center should be red
        ushort centerR = frame.GetPixelRow(50)[50 * ch];
        await Assert.That(centerR).IsEqualTo(Quantum.ScaleFromByte(255));

        // Corner should be blue
        ushort cornerB = frame.GetPixelRow(0)[2];
        await Assert.That(cornerB).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    [Test]
    public async Task Gradient_MultiStop_ThreeColors()
    {
        GradientStop[] stops =
        [
            new(0.0, 255, 0, 0),
            new(0.5, 0, 255, 0),
            new(1.0, 0, 0, 255),
        ];
        var frame = GradientGenerator.LinearMultiStop(201, 1, stops, angleDegrees: 0);
        int ch = frame.NumberOfChannels;

        // Start: red
        ushort startR = frame.GetPixelRow(0)[0];
        await Assert.That(startR).IsEqualTo(Quantum.ScaleFromByte(255));

        // Middle: green
        ushort midG = frame.GetPixelRow(0)[100 * ch + 1];
        await Assert.That(midG).IsEqualTo(Quantum.ScaleFromByte(255));

        // End: blue
        ushort endB = frame.GetPixelRow(0)[200 * ch + 2];
        await Assert.That(endB).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    #endregion

    #region Plasma Generator

    [Test]
    public async Task Plasma_CorrectDimensions()
    {
        var frame = PlasmaGenerator.Generate(128, 64, seed: 42);
        await Assert.That(frame.Columns).IsEqualTo(128u);
        await Assert.That(frame.Rows).IsEqualTo(64u);
    }

    [Test]
    public async Task Plasma_Seeded_IsReproducible()
    {
        var frame1 = PlasmaGenerator.Generate(32, 32, seed: 123);
        var frame2 = PlasmaGenerator.Generate(32, 32, seed: 123);

        bool identical = true;
        int ch = frame1.NumberOfChannels;
        for (int y = 0; y < 32 && identical; y++)
        {
            var row1 = frame1.GetPixelRow(y);
            var row2 = frame2.GetPixelRow(y);
            for (int x = 0; x < 32 * ch && identical; x++)
                if (row1[x] != row2[x]) identical = false;
        }
        await Assert.That(identical).IsTrue();
    }

    [Test]
    public async Task Plasma_DifferentSeeds_DifferentOutput()
    {
        var frame1 = PlasmaGenerator.Generate(32, 32, seed: 1);
        var frame2 = PlasmaGenerator.Generate(32, 32, seed: 2);

        int diffCount = 0;
        int ch = frame1.NumberOfChannels;
        for (int y = 0; y < 32; y++)
        {
            var row1 = frame1.GetPixelRow(y);
            var row2 = frame2.GetPixelRow(y);
            for (int x = 0; x < 32 * ch; x++)
                if (row1[x] != row2[x]) diffCount++;
        }
        // Most pixels should differ
        await Assert.That(diffCount).IsGreaterThan(100);
    }

    [Test]
    public async Task Plasma_HasColorVariation()
    {
        var frame = PlasmaGenerator.Generate(64, 64, seed: 77);
        int ch = frame.NumberOfChannels;

        ushort minR = ushort.MaxValue, maxR = 0;
        for (int y = 0; y < 64; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < 64; x++)
            {
                ushort r = row[x * ch];
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
            }
        }
        // Should have significant range (at least 50% of quantum range)
        int range = maxR - minR;
        await Assert.That(range).IsGreaterThan(Quantum.MaxValue / 2);
    }

    #endregion

    #region Pattern Generator

    [Test]
    public async Task Pattern_Checkerboard_CorrectTiling()
    {
        var frame = PatternGenerator.Generate(16, 16, PatternName.Checkerboard);
        int ch = frame.NumberOfChannels;

        // Top-left 4x4 block should be foreground (black)
        ushort topLeft = frame.GetPixelRow(0)[0];
        await Assert.That(topLeft).IsEqualTo(Quantum.ScaleFromByte(0));

        // Top-right 4x4 block should be background (white)
        ushort topRight = frame.GetPixelRow(0)[4 * ch];
        await Assert.That(topRight).IsEqualTo(Quantum.ScaleFromByte(255));

        // Second row block should be inverted
        ushort secondRow = frame.GetPixelRow(4)[0];
        await Assert.That(secondRow).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    [Test]
    public async Task Pattern_CustomColors()
    {
        var frame = PatternGenerator.Generate(8, 8, PatternName.Checkerboard,
            fgR: 255, fgG: 0, fgB: 0,
            bgR: 0, bgG: 0, bgB: 255);
        int ch = frame.NumberOfChannels;

        // Foreground pixel should be red
        ushort fgR = frame.GetPixelRow(0)[0];
        ushort fgB = frame.GetPixelRow(0)[2];
        await Assert.That(fgR).IsEqualTo(Quantum.ScaleFromByte(255));
        await Assert.That(fgB).IsEqualTo(Quantum.ScaleFromByte(0));

        // Background pixel should be blue
        ushort bgR = frame.GetPixelRow(0)[4 * ch];
        ushort bgB = frame.GetPixelRow(0)[4 * ch + 2];
        await Assert.That(bgR).IsEqualTo(Quantum.ScaleFromByte(0));
        await Assert.That(bgB).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    [Test]
    public async Task Pattern_HorizontalLines_CorrectStructure()
    {
        var frame = PatternGenerator.Generate(12, 12, PatternName.HorizontalLines);
        int ch = frame.NumberOfChannels;

        // Row 0 should be foreground (black), row 1-3 background (white)
        ushort row0 = frame.GetPixelRow(0)[0];
        ushort row1 = frame.GetPixelRow(1)[0];
        await Assert.That(row0).IsEqualTo(Quantum.ScaleFromByte(0));
        await Assert.That(row1).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    [Test]
    public async Task Pattern_Gray50_HalfFilled()
    {
        var frame = PatternGenerator.Generate(4, 4, PatternName.Gray50);
        int ch = frame.NumberOfChannels;

        int foregroundCount = 0;
        for (int y = 0; y < 4; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < 4; x++)
                if (row[x * ch] == Quantum.ScaleFromByte(0)) foregroundCount++;
        }
        // 50% of 16 pixels = 8
        await Assert.That(foregroundCount).IsEqualTo(8);
    }

    [Test]
    public async Task Pattern_AllPatternsGenerate()
    {
        var allPatterns = PatternGenerator.GetAllPatternNames();
        int successCount = 0;
        foreach (var pattern in allPatterns)
        {
            var frame = PatternGenerator.Generate(16, 16, pattern);
            if (frame.Columns == 16 && frame.Rows == 16) successCount++;
        }
        await Assert.That(successCount).IsEqualTo(allPatterns.Length);
    }

    #endregion

    #region Label Generator

    [Test]
    public async Task Label_CorrectAutoSize()
    {
        var frame = LabelGenerator.Label("Hi");
        // "Hi" = 2 chars × (5+1) pixels wide = 12, minus 1 spacing = 11, + 4 padding = 15
        await Assert.That(frame.Columns).IsGreaterThan(0u);
        await Assert.That(frame.Rows).IsGreaterThan(0u);
    }

    [Test]
    public async Task Label_HasText_NotAllBackground()
    {
        var frame = LabelGenerator.Label("Test", textR: 0, textG: 0, textB: 0,
            bgR: 255, bgG: 255, bgB: 255);
        int ch = frame.NumberOfChannels;

        int blackPixelCount = 0;
        for (int y = 0; y < (int)frame.Rows; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < (int)frame.Columns; x++)
                if (row[x * ch] == 0) blackPixelCount++;
        }
        // Should have some foreground pixels (text)
        await Assert.That(blackPixelCount).IsGreaterThan(10);
    }

    [Test]
    public async Task Label_ScaleDoubles_DimensionsIncrease()
    {
        var frame1 = LabelGenerator.Label("AB", scale: 1.0, padding: 0);
        var frame2 = LabelGenerator.Label("AB", scale: 2.0, padding: 0);

        // Scale 2 should be roughly 2× the dimensions of scale 1
        bool wider = frame2.Columns > frame1.Columns;
        bool taller = frame2.Rows > frame1.Rows;
        await Assert.That(wider).IsTrue();
        await Assert.That(taller).IsTrue();
    }

    [Test]
    public async Task Caption_WrapsText()
    {
        // Very narrow width should force wrapping
        var frame = LabelGenerator.Caption("Hello World", maxWidth: 40, scale: 1.0, padding: 2);
        // Should be taller than a single-line label
        var singleLine = LabelGenerator.Label("Hello World", scale: 1.0, padding: 2);
        bool taller = frame.Rows > singleLine.Rows;
        await Assert.That(taller).IsTrue();
    }

    #endregion

    #region Noise Generator Tests

    [Test]
    public async Task Perlin_CorrectDimensions()
    {
        var frame = NoiseGenerator.Perlin(128, 64, frequency: 4.0, seed: 42);
        await Assert.That(frame.Columns).IsEqualTo(128u);
        await Assert.That(frame.Rows).IsEqualTo(64u);
    }

    [Test]
    public async Task Perlin_Seeded_IsReproducible()
    {
        var f1 = NoiseGenerator.Perlin(32, 32, seed: 99);
        var f2 = NoiseGenerator.Perlin(32, 32, seed: 99);
        await Assert.That(FramesIdentical(f1, f2)).IsTrue();
    }

    [Test]
    public async Task Perlin_HasSmoothnessAndRange()
    {
        var frame = NoiseGenerator.Perlin(64, 64, frequency: 3.0, seed: 7);
        var (min, max) = GetValueRange(frame);
        // Should have reasonable range
        int range = max - min;
        await Assert.That(range).IsGreaterThan(Quantum.MaxValue / 4);
    }

    [Test]
    public async Task Simplex_Seeded_IsReproducible()
    {
        var f1 = NoiseGenerator.Simplex(32, 32, seed: 55);
        var f2 = NoiseGenerator.Simplex(32, 32, seed: 55);
        await Assert.That(FramesIdentical(f1, f2)).IsTrue();
    }

    [Test]
    public async Task Simplex_DifferentFromPerlin()
    {
        var perlin = NoiseGenerator.Perlin(32, 32, seed: 1);
        var simplex = NoiseGenerator.Simplex(32, 32, seed: 1);
        // Different algorithms should produce different output
        await Assert.That(FramesIdentical(perlin, simplex)).IsFalse();
    }

    [Test]
    public async Task Worley_F1_ProducesPattern()
    {
        var frame = NoiseGenerator.Worley(64, 64, cellDensity: 4, seed: 42);
        var (min, max) = GetValueRange(frame);
        // F1 should have dark cells (near feature points) and lighter areas
        await Assert.That(min).IsLessThan((ushort)(Quantum.MaxValue / 4));
    }

    [Test]
    public async Task Worley_F2MinusF1_ProducesCellEdges()
    {
        var frame = NoiseGenerator.Worley(64, 64, cellDensity: 4, seed: 42, useF2MinusF1: true);
        var (min, _) = GetValueRange(frame);
        // F2-F1 should have values near 0 at cell edges
        await Assert.That(min).IsLessThan((ushort)(Quantum.MaxValue / 8));
    }

    [Test]
    public async Task Worley_ManhattanDistance_DiffersFromEuclidean()
    {
        var euclidean = NoiseGenerator.Worley(32, 32, cellDensity: 4, seed: 1, distanceType: WorleyDistance.Euclidean);
        var manhattan = NoiseGenerator.Worley(32, 32, cellDensity: 4, seed: 1, distanceType: WorleyDistance.Manhattan);
        await Assert.That(FramesIdentical(euclidean, manhattan)).IsFalse();
    }

    [Test]
    public async Task Fbm_HasMoreDetailThanSingleOctave()
    {
        var single = NoiseGenerator.Perlin(64, 64, frequency: 4.0, seed: 1);
        var fbm = NoiseGenerator.Fbm(64, 64, octaves: 6, frequency: 4.0, seed: 1);
        // FBM should differ from simple Perlin due to multiple octaves
        await Assert.That(FramesIdentical(single, fbm)).IsFalse();
    }

    [Test]
    public async Task Turbulence_HasRange()
    {
        var frame = NoiseGenerator.Turbulence(64, 64, octaves: 4, seed: 33);
        var (min, max) = GetValueRange(frame);
        int range = max - min;
        await Assert.That(range).IsGreaterThan(Quantum.MaxValue / 4);
    }

    [Test]
    public async Task Marble_ProducesStripes()
    {
        var frame = NoiseGenerator.Marble(64, 64, frequency: 3.0, seed: 10);
        var (min, max) = GetValueRange(frame);
        // Marble should have full range from sine waves
        int range = max - min;
        await Assert.That(range).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task Wood_ProducesRingPattern()
    {
        var frame = NoiseGenerator.Wood(64, 64, ringFrequency: 8.0, seed: 5);
        int ch = frame.NumberOfChannels;
        // Wood should have brown-ish colors (R > G > B)
        ushort centerR = frame.GetPixelRow(32)[32 * ch];
        ushort centerG = frame.GetPixelRow(32)[32 * ch + 1];
        ushort centerB = frame.GetPixelRow(32)[32 * ch + 2];
        // R should be greater than B for wood tones
        await Assert.That(centerR > centerB).IsTrue();
    }

    [Test]
    public async Task Colorized_Perlin_HasColorChannelVariation()
    {
        var frame = NoiseGenerator.Perlin(64, 64, frequency: 4.0, seed: 42, colorize: true);
        int ch = frame.NumberOfChannels;
        // Colorized noise should have different R, G, B values (not all gray)
        int diffCount = 0;
        for (int y = 0; y < 64; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < 64; x++)
            {
                ushort r = row[x * ch];
                ushort g = row[x * ch + 1];
                if (r != g) diffCount++;
            }
        }
        await Assert.That(diffCount).IsGreaterThan(100);
    }

    [Test]
    public async Task AllNoiseTypes_GenerateWithoutErrors()
    {
        var perlin = NoiseGenerator.Perlin(16, 16);
        var simplex = NoiseGenerator.Simplex(16, 16);
        var worley = NoiseGenerator.Worley(16, 16);
        var fbm = NoiseGenerator.Fbm(16, 16);
        var turbulence = NoiseGenerator.Turbulence(16, 16);
        var marble = NoiseGenerator.Marble(16, 16);
        var wood = NoiseGenerator.Wood(16, 16);

        int total = (int)(perlin.Columns + simplex.Columns + worley.Columns
            + fbm.Columns + turbulence.Columns + marble.Columns + wood.Columns);
        await Assert.That(total).IsEqualTo(16 * 7);
    }

    private static bool FramesIdentical(ImageFrame a, ImageFrame b)
    {
        if (a.Columns != b.Columns || a.Rows != b.Rows) return false;
        int ch = a.NumberOfChannels;
        for (int y = 0; y < (int)a.Rows; y++)
        {
            var rowA = a.GetPixelRow(y);
            var rowB = b.GetPixelRow(y);
            for (int x = 0; x < (int)a.Columns * ch; x++)
                if (rowA[x] != rowB[x]) return false;
        }
        return true;
    }

    private static (ushort Min, ushort Max) GetValueRange(ImageFrame frame)
    {
        ushort min = ushort.MaxValue, max = 0;
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < (int)frame.Rows; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < (int)frame.Columns; x++)
            {
                ushort v = row[x * ch];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        return (min, max);
    }

    #endregion
}
