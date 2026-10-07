// Film grain estimation for the AV1 encoder: a direct port of libaom's aom_dsp/noise_model.c (flat-block finder,
// Wiener denoiser, auto-regressive noise model and noise-strength solver, grain parameter fit), aom_dsp/noise_util.c,
// the aom_dsp/fft.c float FFTs (kernels generated from fft_common.h) and av1_estimate_noise_from_single_plane
// (av1/encoder/temporal_filter.c). BSD-2, Copyright (c) 2016-2017 Alliance for Open Media. Float / double operation
// order follows the C so the estimated parameters match libaom's (aomenc --denoise-noise-level).

using System;
using SharpImage.Core;

namespace SharpImage.Formats.Av1;

internal static class Av1NoiseModel
{
    private const int LowPolyNumParams = 3;

    // ---------------------------------------------------------------------------------------------------------------
    // Linear algebra (aom_dsp/mathutils.h)
    // ---------------------------------------------------------------------------------------------------------------

    private static bool LinSolve(int n, double[] a, int stride, double[] b, double[] x)
    {
        const double tinyNearZero = 1.0E-16;
        double c;
        for (int k = 0; k < n - 1; k++)
        {
            for (int i = n - 1; i > k; i--)
            {
                if (Math.Abs(a[(i - 1) * stride + k]) < Math.Abs(a[i * stride + k]))
                {
                    for (int j = 0; j < n; j++)
                    {
                        c = a[i * stride + j];
                        a[i * stride + j] = a[(i - 1) * stride + j];
                        a[(i - 1) * stride + j] = c;
                    }
                    c = b[i];
                    b[i] = b[i - 1];
                    b[i - 1] = c;
                }
            }
            for (int i = k; i < n - 1; i++)
            {
                if (Math.Abs(a[k * stride + k]) < tinyNearZero) return false;
                c = a[(i + 1) * stride + k] / a[k * stride + k];
                for (int j = 0; j < n; j++) a[(i + 1) * stride + j] -= c * a[k * stride + j];
                b[i + 1] -= c * b[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            if (Math.Abs(a[i * stride + i]) < tinyNearZero) return false;
            c = 0;
            for (int j = i + 1; j <= n - 1; j++) c += a[i * stride + j] * x[j];
            x[i] = (b[i] - c) / a[i * stride + i];
        }
        return true;
    }

    private static void MultiplyMat(double[] m1, double[] m2, double[] res, int m1Rows, int innerDim, int m2Cols)
    {
        int r = 0;
        for (int row = 0; row < m1Rows; ++row)
            for (int col = 0; col < m2Cols; ++col)
            {
                double sum = 0;
                for (int inner = 0; inner < innerDim; ++inner)
                    sum += m1[row * innerDim + inner] * m2[inner * m2Cols + col];
                res[r++] = sum;
            }
    }

    // AOMMAX / AOMMIN macro semantics (differ from Math.Max/Min only for NaN and signed zeros).
    private static double AMax(double a, double b) => a > b ? a : b;
    private static double AMin(double a, double b) => a < b ? a : b;
    private static int AMax(int a, int b) => a > b ? a : b;
    private static int AMin(int a, int b) => a < b ? a : b;

    private static double FClamp(double value, double low, double high) => value < low ? low : (value > high ? high : value);

    private sealed class EquationSystem
    {
        public double[] A, B, X;
        public int N;
        public EquationSystem(int n) { N = n; A = new double[n * n]; B = new double[n]; X = new double[n]; }
        public void Clear() { Array.Clear(A); Array.Clear(B); Array.Clear(X); }
        public void CopyFrom(EquationSystem s) { Array.Copy(s.A, A, A.Length); Array.Copy(s.X, X, X.Length); Array.Copy(s.B, B, B.Length); }

        public bool Solve()
        {
            var b = (double[])B.Clone();
            var a = (double[])A.Clone();
            if (!LinSolve(N, a, N, b, X)) return false;
            for (int i = 0; i < N; ++i)
                if (float.IsNaN((float)X[i])) return false;
            return true;
        }

        public void Add(EquationSystem src)
        {
            for (int i = 0; i < N; ++i)
            {
                for (int j = 0; j < N; ++j) A[i * N + j] += src.A[i * N + j];
                B[i] += src.B[i];
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Noise strength solver / piecewise-linear LUT
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class StrengthSolver
    {
        public EquationSystem Eqns;
        public double MinIntensity, MaxIntensity, Total;
        public int NumBins, NumEquations;

        public StrengthSolver(int numBins, int bitDepth)
        {
            NumBins = numBins;
            MinIntensity = 0;
            MaxIntensity = (1 << bitDepth) - 1;
            Eqns = new EquationSystem(numBins);
        }

        public void Clear() { Eqns.Clear(); NumEquations = 0; Total = 0; }

        public void Add(StrengthSolver src)
        {
            Eqns.Add(src.Eqns);
            NumEquations += src.NumEquations;
            Total += src.Total;
        }

        public double BinIndex(double value)
        {
            double val = FClamp(value, MinIntensity, MaxIntensity);
            double range = MaxIntensity - MinIntensity;
            return (NumBins - 1) * (val - MinIntensity) / range;
        }

        public double Value(double x)
        {
            double bin = BinIndex(x);
            int i0 = (int)Math.Floor(bin);
            int i1 = AMin(NumBins - 1, i0 + 1);
            double a = bin - i0;
            return (1.0 - a) * Eqns.X[i0] + a * Eqns.X[i1];
        }

        public void AddMeasurement(double blockMean, double noiseStd)
        {
            double bin = BinIndex(blockMean);
            int i0 = (int)Math.Floor(bin);
            int i1 = AMin(NumBins - 1, i0 + 1);
            double a = bin - i0;
            int n = NumBins;
            Eqns.A[i0 * n + i0] += (1.0 - a) * (1.0 - a);
            Eqns.A[i1 * n + i0] += a * (1.0 - a);
            Eqns.A[i1 * n + i1] += a * a;
            Eqns.A[i0 * n + i1] += a * (1.0 - a);
            Eqns.B[i0] += (1.0 - a) * noiseStd;
            Eqns.B[i1] += a * noiseStd;
            Total += noiseStd;
            NumEquations++;
        }

        // aom_noise_strength_solver_solve: regularises a copy of A; b is adjusted in place (as in libaom).
        public bool Solve()
        {
            int n = NumBins;
            double kAlpha = 2.0 * NumEquations / n;
            double[] oldA = Eqns.A;
            var a = (double[])oldA.Clone();
            for (int i = 0; i < n; ++i)
            {
                int iLo = AMax(0, i - 1);
                int iHi = AMin(n - 1, i + 1);
                a[i * n + iLo] -= kAlpha;
                a[i * n + i] += 2 * kAlpha;
                a[i * n + iHi] -= kAlpha;
            }
            double mean = Total / NumEquations;
            for (int i = 0; i < n; ++i)
            {
                a[i * n + i] += 1.0 / 8192.0;
                Eqns.B[i] += mean / 8192.0;
            }
            Eqns.A = a;
            bool result = Eqns.Solve();
            Eqns.A = oldA;
            return result;
        }

        public double Center(int i)
        {
            double range = MaxIntensity - MinIntensity;
            return (double)i / (NumBins - 1) * range + MinIntensity;
        }
    }

    private static void UpdatePiecewiseLinearResidual(StrengthSolver solver, double[][] points, int numPoints,
                                                      double[] residual, int start, int end)
    {
        double dx = 255.0 / solver.NumBins;
        for (int i = AMax(start, 1); i < AMin(end, numPoints - 1); ++i)
        {
            int lower = AMax(0, (int)Math.Floor(solver.BinIndex(points[i - 1][0])));
            int upper = AMin(solver.NumBins - 1, (int)Math.Ceiling(solver.BinIndex(points[i + 1][0])));
            double r = 0;
            for (int j = lower; j <= upper; ++j)
            {
                double x = solver.Center(j);
                if (x < points[i - 1][0]) continue;
                if (x >= points[i + 1][0]) continue;
                double y = solver.Eqns.X[j];
                double a = (x - points[i - 1][0]) / (points[i + 1][0] - points[i - 1][0]);
                double estimateY = points[i - 1][1] * (1.0 - a) + points[i + 1][1] * a;
                r += Math.Abs(y - estimateY);
            }
            residual[i] = r * dx;
        }
    }

    // aom_noise_strength_solver_fit_piecewise. Note the residual array is not compacted when a point is removed
    // (libaom behaviour, reproduced).
    private static double[][] FitPiecewise(StrengthSolver solver, int maxOutputPoints)
    {
        double kTolerance = solver.MaxIntensity * 0.00625 / 255.0;
        var points = new double[solver.NumBins][];
        for (int i = 0; i < solver.NumBins; ++i) points[i] = [solver.Center(i), solver.Eqns.X[i]];
        int numPoints = solver.NumBins;
        if (maxOutputPoints < 0) maxOutputPoints = solver.NumBins;
        var residual = new double[solver.NumBins];
        UpdatePiecewiseLinearResidual(solver, points, numPoints, residual, 0, solver.NumBins);
        while (numPoints > 2)
        {
            int minIndex = 1;
            for (int j = 1; j < numPoints - 1; ++j)
                if (residual[j] < residual[minIndex]) minIndex = j;
            double dx = points[minIndex + 1][0] - points[minIndex - 1][0];
            double avgResidual = residual[minIndex] / dx;
            if (numPoints <= maxOutputPoints && avgResidual > kTolerance) break;
            int numRemaining = numPoints - minIndex - 1;
            for (int k = 0; k < numRemaining; k++) points[minIndex + k] = (double[])points[minIndex + k + 1].Clone();
            numPoints--;
            UpdatePiecewiseLinearResidual(solver, points, numPoints, residual, minIndex - 1, minIndex + 1);
        }
        return points[..numPoints];
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Flat block finder
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class FlatBlockFinder
    {
        public readonly double[] A, AtAInv;
        public readonly int BlockSize;
        public readonly double Normalization;

        public FlatBlockFinder(int blockSize, int bitDepth)
        {
            int n = blockSize * blockSize;
            var eqns = new EquationSystem(LowPolyNumParams);
            AtAInv = new double[LowPolyNumParams * LowPolyNumParams];
            A = new double[LowPolyNumParams * n];
            BlockSize = blockSize;
            Normalization = (1 << bitDepth) - 1;
            Span<double> coords = stackalloc double[3];
            for (int y = 0; y < blockSize; ++y)
            {
                double yd = ((double)y - blockSize / 2.0) / (blockSize / 2.0);
                for (int x = 0; x < blockSize; ++x)
                {
                    double xd = ((double)x - blockSize / 2.0) / (blockSize / 2.0);
                    coords[0] = yd; coords[1] = xd; coords[2] = 1;
                    int row = y * blockSize + x;
                    A[LowPolyNumParams * row + 0] = yd;
                    A[LowPolyNumParams * row + 1] = xd;
                    A[LowPolyNumParams * row + 2] = 1;
                    for (int i = 0; i < LowPolyNumParams; ++i)
                        for (int j = 0; j < LowPolyNumParams; ++j)
                            eqns.A[LowPolyNumParams * i + j] += coords[i] * coords[j];
                }
            }
            for (int i = 0; i < LowPolyNumParams; ++i)
            {
                Array.Clear(eqns.B);
                eqns.B[i] = 1;
                if (!eqns.Solve()) throw new InvalidOperationException("Flat block finder init failed.");
                for (int j = 0; j < LowPolyNumParams; ++j) AtAInv[j * LowPolyNumParams + i] = eqns.X[j];
            }
        }

        public void ExtractBlock(ushort[] data, int w, int h, int stride, int offsx, int offsy, double[] plane, double[] block)
        {
            int bs = BlockSize, n = bs * bs;
            var atAInvB = new double[LowPolyNumParams];
            var planeCoords = new double[LowPolyNumParams];
            for (int yi = 0; yi < bs; ++yi)
            {
                int y = Math.Clamp(offsy + yi, 0, h - 1);
                for (int xi = 0; xi < bs; ++xi)
                {
                    int x = Math.Clamp(offsx + xi, 0, w - 1);
                    block[yi * bs + xi] = data[y * stride + x] / Normalization;
                }
            }
            MultiplyMat(block, A, atAInvB, 1, n, LowPolyNumParams);
            MultiplyMat(AtAInv, atAInvB, planeCoords, LowPolyNumParams, LowPolyNumParams, 1);
            MultiplyMat(A, planeCoords, plane, n, LowPolyNumParams, 1);
            for (int i = 0; i < n; ++i) block[i] -= plane[i];
        }

        public int Run(ushort[] data, int w, int h, int stride, byte[] flatBlocks)
        {
            int bs = BlockSize, n = bs * bs;
            const double kTraceThreshold = 0.15 / (32 * 32);
            const double kRatioThreshold = 1.25;
            const double kNormThreshold = 0.08 / (32 * 32);
            double kVarThreshold = 0.005 / (double)n;
            int numBlocksW = (w + bs - 1) / bs, numBlocksH = (h + bs - 1) / bs;
            int numFlat = 0;
            var plane = new double[n];
            var block = new double[n];
            var scores = new float[numBlocksW * numBlocksH];
            var index = new int[numBlocksW * numBlocksH];
            for (int by = 0; by < numBlocksH; ++by)
            {
                for (int bx = 0; bx < numBlocksW; ++bx)
                {
                    ExtractBlock(data, w, h, stride, bx * bs, by * bs, plane, block);
                    double gxx = 0, gxy = 0, gyy = 0, mean = 0, var = 0;
                    for (int yi = 1; yi < bs - 1; ++yi)
                        for (int xi = 1; xi < bs - 1; ++xi)
                        {
                            double gx = (block[yi * bs + xi + 1] - block[yi * bs + xi - 1]) / 2;
                            double gy = (block[yi * bs + xi + bs] - block[yi * bs + xi - bs]) / 2;
                            gxx += gx * gx;
                            gxy += gx * gy;
                            gyy += gy * gy;
                            double value = block[yi * bs + xi];
                            mean += value;
                            var += value * value;
                        }
                    mean /= (bs - 2) * (bs - 2);
                    gxx /= (bs - 2) * (bs - 2);
                    gxy /= (bs - 2) * (bs - 2);
                    gyy /= (bs - 2) * (bs - 2);
                    var = var / ((bs - 2) * (bs - 2)) - mean * mean;

                    double trace = gxx + gyy;
                    double det = gxx * gyy - gxy * gxy;
                    double e1 = (trace + Math.Sqrt(trace * trace - 4 * det)) / 2.0;
                    double e2 = (trace - Math.Sqrt(trace * trace - 4 * det)) / 2.0;
                    double norm = e1;
                    double ratio = e1 / AMax(e2, 1e-6);
                    bool isFlat = trace < kTraceThreshold && ratio < kRatioThreshold && norm < kNormThreshold && var > kVarThreshold;
                    double sumWeights = -6682 * var + -0.2056 * ratio + 13087 * trace + -12434 * norm + 2.5694;
                    sumWeights = FClamp(sumWeights, -25.0, 100.0);
                    float score = (float)(1.0 / (1 + PortableMathD.Exp(-sumWeights)));
                    flatBlocks[by * numBlocksW + bx] = isFlat ? (byte)255 : (byte)0;
                    scores[by * numBlocksW + bx] = var > kVarThreshold ? score : 0;
                    index[by * numBlocksW + bx] = by * numBlocksW + bx;
                    numFlat += isFlat ? 1 : 0;
                }
            }
            // Union of the thresholded result and the top 10% of scores (only the sorted values matter).
            var sorted = (float[])scores.Clone();
            Array.Sort(sorted);
            int topNth = numBlocksW * numBlocksH * 90 / 100;
            float scoreThreshold = sorted[topNth];
            for (int i = 0; i < scores.Length; ++i)
            {
                if (scores[i] >= scoreThreshold)
                {
                    numFlat += flatBlocks[index[i]] == 0 ? 1 : 0;
                    flatBlocks[index[i]] |= 1;
                }
            }
            return numFlat;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Auto-regressive noise model (square shape, lag 3: 24 coefficients, +1 luma correlation for chroma)
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class NoiseState
    {
        public EquationSystem Eqns;
        public StrengthSolver Strength;
        public int NumObservations;
        public double ArGain = 1.0;
        public NoiseState(int n, int bitDepth) { Eqns = new EquationSystem(n); Strength = new StrengthSolver(20, bitDepth); }
    }

    private sealed class NoiseModel
    {
        public readonly int Lag = 3, BitDepth, N;
        public readonly int[][] Coords;
        public readonly NoiseState[] Combined = new NoiseState[3], Latest = new NoiseState[3];

        public NoiseModel(int bitDepth)
        {
            BitDepth = bitDepth;
            int side = 2 * Lag + 1;
            N = side * side / 2;
            for (int c = 0; c < 3; ++c)
            {
                Combined[c] = new NoiseState(N + (c > 0 ? 1 : 0), bitDepth);
                Latest[c] = new NoiseState(N + (c > 0 ? 1 : 0), bitDepth);
            }
            Coords = new int[N][];
            int i = 0;
            for (int y = -Lag; y <= 0; ++y)
            {
                int maxX = y == 0 ? -1 : Lag;
                for (int x = -Lag; x <= maxX; ++x) Coords[i++] = [x, y];
            }
        }
    }

    private static double BlockMean(ushort[] data, int w, int h, int stride, int xo, int yo, int blockSize)
    {
        int maxH = AMin(h - yo, blockSize), maxW = AMin(w - xo, blockSize);
        double blockMean = 0;
        for (int y = 0; y < maxH; ++y)
            for (int x = 0; x < maxW; ++x)
                blockMean += data[(yo + y) * stride + xo + x];
        return blockMean / (maxW * maxH);
    }

    private static double NoiseVar(ushort[] data, ushort[] denoised, int stride, int w, int h, int xo, int yo, int bsx, int bsy)
    {
        int maxH = AMin(h - yo, bsy), maxW = AMin(w - xo, bsx);
        double noiseVar = 0, noiseMean = 0;
        for (int y = 0; y < maxH; ++y)
            for (int x = 0; x < maxW; ++x)
            {
                double noise = (double)data[(yo + y) * stride + xo + x] - denoised[(yo + y) * stride + xo + x];
                noiseMean += noise;
                noiseVar += noise * noise;
            }
        noiseMean /= maxW * maxH;
        return noiseVar / (maxW * maxH) - noiseMean * noiseMean;
    }

    private static double ExtractArRow(int[][] coords, int numCoords, ushort[] data, ushort[] denoised, int stride, int subX,
        int subY, ushort[]? altData, ushort[]? altDenoised, int altStride, int x, int y, double[] buffer)
    {
        for (int i = 0; i < numCoords; ++i)
        {
            int xi = x + coords[i][0], yi = y + coords[i][1];
            buffer[i] = (double)data[yi * stride + xi] - denoised[yi * stride + xi];
        }
        double val = (double)data[y * stride + x] - denoised[y * stride + x];
        if (altData != null && altDenoised != null)
        {
            double avgData = 0, avgDenoised = 0;
            int numSamples = 0;
            for (int dyi = 0; dyi < (1 << subY); dyi++)
            {
                int yUp = (y << subY) + dyi;
                for (int dxi = 0; dxi < (1 << subX); dxi++)
                {
                    int xUp = (x << subX) + dxi;
                    avgData += altData[yUp * altStride + xUp];
                    avgDenoised += altDenoised[yUp * altStride + xUp];
                    numSamples++;
                }
            }
            buffer[numCoords] = (avgData - avgDenoised) / numSamples;
        }
        return val;
    }

    private static void AddBlockObservations(NoiseModel model, int c, ushort[] data, ushort[] denoised, int w, int h,
        int stride, int subX, int subY, ushort[]? altData, ushort[]? altDenoised, int altStride, byte[] flatBlocks,
        int blockSize, int numBlocksW, int numBlocksH)
    {
        int lag = model.Lag, numCoords = model.N;
        double normalization = (1 << model.BitDepth) - 1;
        var st = model.Latest[c];
        double[] a = st.Eqns.A, b = st.Eqns.B;
        var buffer = new double[numCoords + 1];
        int n = st.Eqns.N;
        for (int by = 0; by < numBlocksH; ++by)
        {
            int yo = by * (blockSize >> subY);
            for (int bx = 0; bx < numBlocksW; ++bx)
            {
                int xo = bx * (blockSize >> subX);
                if (flatBlocks[by * numBlocksW + bx] == 0) continue;
                int yStart = by > 0 && flatBlocks[(by - 1) * numBlocksW + bx] != 0 ? 0 : lag;
                int xStart = bx > 0 && flatBlocks[by * numBlocksW + bx - 1] != 0 ? 0 : lag;
                int yEnd = AMin((h >> subY) - by * (blockSize >> subY), blockSize >> subY);
                int xEnd = AMin((w >> subX) - bx * (blockSize >> subX) - lag,
                    bx + 1 < numBlocksW && flatBlocks[by * numBlocksW + bx + 1] != 0 ? blockSize >> subX : (blockSize >> subX) - lag);
                for (int y = yStart; y < yEnd; ++y)
                    for (int x = xStart; x < xEnd; ++x)
                    {
                        double val = ExtractArRow(model.Coords, numCoords, data, denoised, stride, subX, subY, altData,
                            altDenoised, altStride, x + xo, y + yo, buffer);
                        for (int i = 0; i < n; ++i)
                        {
                            for (int j = 0; j < n; ++j)
                                a[i * n + j] += buffer[i] * buffer[j] / (normalization * normalization);
                            b[i] += buffer[i] * val / (normalization * normalization);
                        }
                        st.NumObservations++;
                    }
            }
        }
    }

    private static void AddNoiseStdObservations(NoiseModel model, int c, double[] coeffs, ushort[] data, ushort[] denoised,
        int w, int h, int stride, int subX, int subY, ushort[]? altData, int altStride, byte[] flatBlocks, int blockSize,
        int numBlocksW, int numBlocksH)
    {
        int numCoords = model.N;
        var solver = model.Latest[c].Strength;
        var lumaSolver = model.Latest[0].Strength;
        double lumaGain = model.Latest[0].ArGain, noiseGain = model.Latest[c].ArGain;
        for (int by = 0; by < numBlocksH; ++by)
        {
            int yo = by * (blockSize >> subY);
            for (int bx = 0; bx < numBlocksW; ++bx)
            {
                int xo = bx * (blockSize >> subX);
                if (flatBlocks[by * numBlocksW + bx] == 0) continue;
                int numSamplesH = AMin((h >> subY) - by * (blockSize >> subY), blockSize >> subY);
                int numSamplesW = AMin((w >> subX) - bx * (blockSize >> subX), blockSize >> subX);
                if (numSamplesW * numSamplesH > blockSize)
                {
                    double blockMean = BlockMean(altData ?? data, w, h, altData != null ? altStride : stride,
                        xo << subX, yo << subY, blockSize);
                    double noiseVar = NoiseVar(data, denoised, stride, w >> subX, h >> subY, xo, yo, blockSize >> subX, blockSize >> subY);
                    double lumaStrength = c > 0 ? lumaGain * lumaSolver.Value(blockMean) : 0;
                    double corr = c > 0 ? coeffs[numCoords] : 0;
                    double uncorrStd = Math.Sqrt(AMax(noiseVar / 16, noiseVar - PortableMathD.Pow(corr * lumaStrength, 2)));
                    double adjustedStrength = uncorrStd / noiseGain;
                    solver.AddMeasurement(blockMean, adjustedStrength);
                }
            }
        }
    }

    private static bool ArEquationSystemSolve(NoiseState state, bool isChroma)
    {
        bool ret = state.Eqns.Solve();
        state.ArGain = 1.0;
        if (!ret) return false;
        int ic = isChroma ? 1 : 0;
        double var = 0;
        int n = state.Eqns.N;
        for (int i = 0; i < n - ic; ++i) var += state.Eqns.A[i * n + i] / state.NumObservations;
        var /= n - ic;
        double sumCovar = 0;
        for (int i = 0; i < n - ic; ++i)
        {
            double bi = state.Eqns.B[i];
            if (isChroma) bi -= state.Eqns.A[i * n + (n - 1)] * state.Eqns.X[n - 1];
            sumCovar += bi * state.Eqns.X[i] / state.NumObservations;
        }
        double noiseVar = AMax(var - sumCovar, 1e-6);
        state.ArGain = AMax(1, Math.Sqrt(AMax(var / noiseVar, 1e-6)));
        return true;
    }

    private static void ChromaFallback(EquationSystem eqns)
    {
        const double kTolerance = 1e-6;
        int last = eqns.N - 1;
        Array.Clear(eqns.X);
        if (Math.Abs(eqns.A[last * eqns.N + last]) > kTolerance) eqns.X[last] = eqns.B[last] / eqns.A[last * eqns.N + last];
    }

    // aom_noise_model_update for the first (only) frame of a still image: the combined state equals the latest.
    private static bool ModelUpdate(NoiseModel model, ushort[]?[] data, ushort[]?[] denoised, int w, int h, int[] stride,
        int subX, int subY, byte[] flatBlocks, int blockSize)
    {
        int numBlocksW = (w + blockSize - 1) / blockSize, numBlocksH = (h + blockSize - 1) / blockSize;
        if (blockSize <= 1 || blockSize < model.Lag * 2 + 1) { LastStatus = "invalid block size"; return false; }
        for (int i = 0; i < 3; ++i)
        {
            model.Latest[i].Eqns.Clear();
            model.Latest[i].NumObservations = 0;
            model.Latest[i].Strength.Clear();
        }
        int numBlocks = 0;
        for (int i = 0; i < numBlocksH * numBlocksW; ++i) if (flatBlocks[i] != 0) numBlocks++;
        if (numBlocks <= 1) { LastStatus = "insufficient flat blocks"; return false; }   // AOM_NOISE_STATUS_INSUFFICIENT_FLAT_BLOCKS

        for (int ch = 0; ch < 3; ++ch)
        {
            if (data[ch] == null || denoised[ch] == null) break;
            ushort[]? altData = ch > 0 ? data[0] : null, altDenoised = ch > 0 ? denoised[0] : null;
            int sx = ch > 0 ? subX : 0, sy = ch > 0 ? subY : 0;
            bool isChroma = ch != 0;
            AddBlockObservations(model, ch, data[ch]!, denoised[ch]!, w, h, stride[ch], sx, sy, altData, altDenoised,
                stride[0], flatBlocks, blockSize, numBlocksW, numBlocksH);
            if (!ArEquationSystemSolve(model.Latest[ch], isChroma))
            {
                if (isChroma) ChromaFallback(model.Latest[ch].Eqns);
                else { LastStatus = "luma AR solve failed"; return false; }
            }
            AddNoiseStdObservations(model, ch, model.Latest[ch].Eqns.X, data[ch]!, denoised[ch]!, w, h, stride[ch], sx, sy,
                altData, stride[0], flatBlocks, blockSize, numBlocksW, numBlocksH);
            if (!model.Latest[ch].Strength.Solve()) { LastStatus = "latest strength solve failed"; return false; }

            // combined_state has no equations yet, so the "different noise type" check cannot trigger.
            model.Combined[ch].NumObservations += model.Latest[ch].NumObservations;
            model.Combined[ch].Eqns.Add(model.Latest[ch].Eqns);
            if (!ArEquationSystemSolve(model.Combined[ch], isChroma))
            {
                if (isChroma) ChromaFallback(model.Combined[ch].Eqns);
                else { LastStatus = "combined luma AR solve failed"; return false; }
            }
            model.Combined[ch].Strength.Add(model.Latest[ch].Strength);
            if (!model.Combined[ch].Strength.Solve()) { LastStatus = "combined strength solve failed"; return false; }
        }
        return true;
    }

    // aom_noise_model_get_grain_parameters.
    private static AvifFilmGrain GrainParameters(NoiseModel model)
    {
        var g = new AvifFilmGrain { ArCoeffLag = model.Lag };
        var scaling = new double[3][][];
        scaling[0] = FitPiecewise(model.Combined[0].Strength, 14);
        scaling[1] = FitPiecewise(model.Combined[1].Strength, 10);
        scaling[2] = FitPiecewise(model.Combined[2].Strength, 10);

        double strengthDivisor = 1 << (model.BitDepth - 8);
        double maxScalingValue = 1e-4;
        for (int c = 0; c < 3; ++c)
            foreach (var p in scaling[c])
            {
                p[0] = AMin(255, p[0] / strengthDivisor);
                p[1] = AMin(255, p[1] / strengthDivisor);
                maxScalingValue = AMax(p[1], maxScalingValue);
            }
        int maxScalingValueLog2 = Math.Clamp((int)Math.Floor(PortableMathD.Log2(maxScalingValue) + 1), 2, 5);
        g.ScalingShift = 5 + (8 - maxScalingValueLog2);
        double scaleFactor = 1 << (8 - maxScalingValueLog2);
        (int, int)[] Convert(double[][] pts)
        {
            var r = new (int, int)[pts.Length];
            for (int i = 0; i < pts.Length; ++i)
                r[i] = ((int)(pts[i][0] + 0.5), Math.Clamp((int)(scaleFactor * pts[i][1] + 0.5), 0, 255));
            return r;
        }
        g.ScalingPointsY = Convert(scaling[0]);
        g.ScalingPointsCb = Convert(scaling[1]);
        g.ScalingPointsCr = Convert(scaling[2]);

        int nCoeff = model.Combined[0].Eqns.N;
        double maxCoeff = 1e-4, minCoeff = -1e-4;
        var yCorr = new double[2];
        double avgLumaStrength = 0;
        for (int c = 0; c < 3; c++)
        {
            var eqns = model.Combined[c].Eqns;
            for (int i = 0; i < nCoeff; ++i)
            {
                maxCoeff = AMax(maxCoeff, eqns.X[i]);
                minCoeff = AMin(minCoeff, eqns.X[i]);
            }
            var solver = model.Combined[c].Strength;
            double averageStrength = 0, totalWeight = 0;
            for (int i = 0; i < solver.Eqns.N; ++i)
            {
                double wgt = 0;
                for (int j = 0; j < solver.Eqns.N; ++j) wgt += solver.Eqns.A[i * solver.Eqns.N + j];
                wgt = Math.Sqrt(wgt);
                averageStrength += solver.Eqns.X[i] * wgt;
                totalWeight += wgt;
            }
            if (totalWeight == 0) averageStrength = 1;
            else averageStrength /= totalWeight;
            if (c == 0) avgLumaStrength = averageStrength;
            else
            {
                yCorr[c - 1] = averageStrength > 1e-6 ? avgLumaStrength * eqns.X[nCoeff] / averageStrength : 0;
                maxCoeff = AMax(maxCoeff, yCorr[c - 1]);
                minCoeff = AMin(minCoeff, yCorr[c - 1]);
            }
        }
        g.ArCoeffShift = Math.Clamp(7 - (int)AMax(1 + Math.Floor(PortableMathD.Log2(maxCoeff)), Math.Ceiling(PortableMathD.Log2(-minCoeff))), 6, 9);
        double scaleAr = 1 << g.ArCoeffShift;
        int[][] ar = [g.ArCoeffsY, g.ArCoeffsCb, g.ArCoeffsCr];
        for (int c = 0; c < 3; ++c)
        {
            var eqns = model.Combined[c].Eqns;
            for (int i = 0; i < nCoeff; ++i)
                ar[c][i] = Math.Clamp((int)Math.Round(scaleAr * eqns.X[i], MidpointRounding.AwayFromZero), -128, 127);
            if (c > 0)
                ar[c][nCoeff] = Math.Clamp((int)Math.Round(scaleAr * yCorr[c - 1], MidpointRounding.AwayFromZero), -128, 127);
        }
        g.CbMult = 128; g.CbLumaMult = 192; g.CbOffset = 256;
        g.CrMult = 128; g.CrLumaMult = 192; g.CrOffset = 256;
        g.ChromaScalingFromLuma = false;
        g.GrainScaleShift = 0;
        g.OverlapFlag = true;
        g.ClipToRestrictedRange = false;
        return g;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Float FFT (aom_dsp/fft.c) and the Wiener noise transform (aom_dsp/noise_util.c)
    // ---------------------------------------------------------------------------------------------------------------

    private delegate void Fft1d(float[] input, int io, float[] output, int oo, int stride);

    private static void Transpose(float[] a, float[] b, int n)
    {
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                b[y * n + x] = a[x * n + y];
    }

    private static void Unpack2dOutput(float[] colFft, float[] output, int n)
    {
        for (int y = 0; y <= n / 2; ++y)
        {
            int y2 = y + n / 2;
            bool yExtra = y2 > n / 2 && y2 < n;
            for (int x = 0; x <= n / 2; ++x)
            {
                int x2 = x + n / 2;
                bool xExtra = x2 > n / 2 && x2 < n;
                output[2 * (y * n + x)] = colFft[y * n + x] - (xExtra && yExtra ? colFft[y2 * n + x2] : 0);
                output[2 * (y * n + x) + 1] = (yExtra ? colFft[y2 * n + x] : 0) + (xExtra ? colFft[y * n + x2] : 0);
                if (yExtra)
                {
                    output[2 * ((n - y) * n + x)] = colFft[y * n + x] + (xExtra && yExtra ? colFft[y2 * n + x2] : 0);
                    output[2 * ((n - y) * n + x) + 1] = -(yExtra ? colFft[y2 * n + x] : 0) + (xExtra ? colFft[y * n + x2] : 0);
                }
            }
        }
    }

    private static void Fft2d(float[] input, float[] temp, float[] output, int n, Fft1d tform)
    {
        for (int x = 0; x < n; x++) tform(input, x, output, x, n);
        Transpose(output, temp, n);
        for (int x = 0; x < n; x++) tform(temp, x, output, x, n);
        Transpose(output, temp, n);
        Unpack2dOutput(temp, output, n);
    }

    private static void Ifft2d(float[] input, float[] temp, float[] output, int n, Fft1d fft, Fft1d ifft)
    {
        for (int y = 0; y <= n / 2; ++y)
        {
            output[y * n] = input[2 * y * n];
            output[y * n + 1] = input[2 * (y * n + n / 2)];
        }
        for (int y = n / 2 + 1; y < n; ++y)
        {
            output[y * n] = input[2 * (y - n / 2) * n + 1];
            output[y * n + 1] = input[2 * ((y - n / 2) * n + n / 2) + 1];
        }
        for (int i = 0; i < 2; i++) ifft(output, i, temp, i, n);
        for (int y = 0; y < n; ++y)
        {
            for (int x = 1; x < n / 2; ++x) output[y * n + (x + 1)] = input[2 * (y * n + x)];
            for (int x = 1; x < n / 2; ++x) output[y * n + (x + n / 2)] = input[2 * (y * n + x) + 1];
        }
        for (int y = 2; y < n; y++) fft(output, y, temp, y, n);
        for (int x = 0; x < n; ++x)
        {
            output[x] = temp[x * n];
            output[(n / 2) * n + x] = temp[x * n + 1];
        }
        for (int y = 1; y < n / 2; ++y)
        {
            for (int x = 0; x <= n / 2; ++x)
                output[x + y * n] = temp[(y + 1) + x * n] + (x > 0 && x < n / 2 ? temp[(y + n / 2) + (x + n / 2) * n] : 0);
            for (int x = n / 2 + 1; x < n; ++x)
                output[x + y * n] = temp[(y + 1) + (n - x) * n] - temp[(y + n / 2) + ((n - x) + n / 2) * n];
            for (int x = 0; x <= n / 2; ++x)
                output[x + (y + n / 2) * n] = temp[(y + n / 2) + x * n] - (x > 0 && x < n / 2 ? temp[(y + 1) + (x + n / 2) * n] : 0);
            for (int x = n / 2 + 1; x < n; ++x)
                output[x + (y + n / 2) * n] = temp[(y + 1) + ((n - x) + n / 2) * n] + temp[(y + n / 2) + (n - x) * n];
        }
        for (int y = 0; y < n; y++) ifft(output, y, temp, y, n);
        Transpose(temp, output, n);
    }

    private sealed class NoiseTx
    {
        public readonly int BlockSize;
        public readonly float[] TxBlock, Temp;
        private readonly Fft1d fft, ifft;

        public NoiseTx(int blockSize)
        {
            (fft, ifft) = blockSize switch
            {
                2 => ((Fft1d)Fft1d2, (Fft1d)Ifft1d2),
                4 => (Fft1d4, Ifft1d4),
                8 => (Fft1d8, Ifft1d8),
                16 => (Fft1d16, Ifft1d16),
                32 => (Fft1d32, Ifft1d32),
                _ => throw new ArgumentOutOfRangeException(nameof(blockSize), "Denoise block size must be 2, 4, 8, 16 or 32 (per plane)."),
            };
            BlockSize = blockSize;
            TxBlock = new float[2 * blockSize * blockSize];
            Temp = new float[2 * blockSize * blockSize];
        }

        public void Forward(float[] data) => Fft2d(data, Temp, TxBlock, BlockSize, fft);

        public void Filter(float[] psd)
        {
            int bs = BlockSize;
            float kBeta = 1.1f, kEps = 1e-6f;
            float keep = (kBeta - 1.0f) / kBeta;
            for (int y = 0; y < bs; ++y)
                for (int x = 0; x < bs; ++x)
                {
                    int i = y * bs + x;
                    float c0 = MaxF(Math.Abs(TxBlock[2 * i]), 1e-8f);
                    float c1 = MaxF(Math.Abs(TxBlock[2 * i + 1]), 1e-8f);
                    float p = c0 * c0 + c1 * c1;
                    if (p > kBeta * psd[i] && p > 1e-6)
                    {
                        TxBlock[2 * i] *= (p - psd[i]) / MaxF(p, kEps);
                        TxBlock[2 * i + 1] *= (p - psd[i]) / MaxF(p, kEps);
                    }
                    else
                    {
                        TxBlock[2 * i] *= keep;
                        TxBlock[2 * i + 1] *= keep;
                    }
                }
        }

        public void Inverse(float[] data)
        {
            int n = BlockSize * BlockSize;
            Ifft2d(TxBlock, Temp, data, BlockSize, fft, ifft);
            for (int i = 0; i < n; ++i) data[i] /= n;
        }
    }

    private static float MaxF(float a, float b) => a > b ? a : b;

    private static float[] HalfCosWindow(int blockSize)
    {
        var wf = new float[blockSize * blockSize];
        for (int y = 0; y < blockSize; ++y)
        {
            double cosYd = Math.Cos((.5 + y) * Math.PI / blockSize - Math.PI / 2);
            for (int x = 0; x < blockSize; ++x)
            {
                double cosXd = Math.Cos((.5 + x) * Math.PI / blockSize - Math.PI / 2);
                wf[y * blockSize + x] = (float)(cosYd * cosXd);
            }
        }
        return wf;
    }

    private static float PsdDefaultValue(int blockSize, float factor) => factor * factor / 10000 * blockSize * blockSize / 8;

    private static void DitherAndQuantize(float[] result, int resultStride, ushort[] denoised, int w, int h, int stride,
                                          int subW, int subH, int blockSize, float norm)
    {
        for (int y = 0; y < (h >> subH); ++y)
            for (int x = 0; x < (w >> subW); ++x)
            {
                int idx = (y + (blockSize >> subH)) * resultStride + x + (blockSize >> subW);
                float v = result[idx] * norm + 0.5f;
                v = v > 0 ? v : 0;
                ushort newVal = (ushort)(v < norm ? v : norm);
                float err = -((float)newVal / norm - result[idx]);
                if (Math.Abs(err) < 1e-6f) err = 0.0f;
                denoised[y * stride + x] = newVal;
                if (x + 1 < (w >> subW)) result[idx + 1] += err * 7.0f / 16.0f;
                if (y + 1 < (h >> subH))
                {
                    if (x > 0) result[idx + resultStride - 1] += err * 3.0f / 16.0f;
                    result[idx + resultStride] += err * 5.0f / 16.0f;
                    if (x + 1 < (w >> subW)) result[idx + resultStride + 1] += err * 1.0f / 16.0f;
                }
            }
    }

    // aom_wiener_denoise_2d. denoised[c] must start as a copy of data[c] (libaom leaves the columns / rows beyond
    // (w >> sub) uninitialised; we keep the source there).
    private static void WienerDenoise2d(ushort[]?[] data, ushort[]?[] denoised, int w, int h, int[] stride, int subX, int subY,
                                        float[][] noisePsd, int blockSize, int bitDepth)
    {
        int numBlocksW = (w + blockSize - 1) / blockSize, numBlocksH = (h + blockSize - 1) / blockSize;
        int resultStride = (numBlocksW + 2) * blockSize, resultHeight = (numBlocksH + 2) * blockSize;
        float norm = (float)((1 << bitDepth) - 1);
        var finderFull = new FlatBlockFinder(blockSize, bitDepth);
        var result = new float[resultHeight * resultStride];
        var plane = new float[blockSize * blockSize];
        var block = new float[2 * blockSize * blockSize];
        var blockD = new double[blockSize * blockSize];
        var planeD = new double[blockSize * blockSize];
        var windowFull = HalfCosWindow(blockSize);
        var txFull = new NoiseTx(blockSize);
        FlatBlockFinder finderChroma = finderFull;
        float[] windowChroma = windowFull;
        NoiseTx txChroma = txFull;
        if (subX != 0)
        {
            finderChroma = new FlatBlockFinder(blockSize >> subX, bitDepth);
            windowChroma = HalfCosWindow(blockSize >> subX);
            txChroma = new NoiseTx(blockSize >> subX);
        }
        for (int c = 0; c < 3; ++c)
        {
            if (data[c] == null || denoised[c] == null) continue;
            float[] window = c == 0 ? windowFull : windowChroma;
            int subH = c > 0 ? subY : 0, subW = c > 0 ? subX : 0;
            NoiseTx tx = c > 0 && subX > 0 ? txChroma : txFull;
            FlatBlockFinder finder = c > 0 && subX != 0 ? finderChroma : finderFull;
            Array.Clear(result);
            int bh = blockSize >> subH, bw = blockSize >> subW;
            for (int offsy = 0; offsy < bh; offsy += bh / 2)
                for (int offsx = 0; offsx < bw; offsx += bw / 2)
                    for (int by = -1; by < numBlocksH; ++by)
                        for (int bx = -1; bx < numBlocksW; ++bx)
                        {
                            int pixels = bw * bh;
                            finder.ExtractBlock(data[c]!, w >> subW, h >> subH, stride[c], bx * bw + offsx, by * bh + offsy, planeD, blockD);
                            for (int j = 0; j < pixels; ++j)
                            {
                                block[j] = (float)blockD[j];
                                plane[j] = (float)planeD[j];
                            }
                            for (int j = 0; j < pixels; ++j) block[j] *= window[j];
                            tx.Forward(block);
                            tx.Filter(noisePsd[c]);
                            tx.Inverse(block);
                            for (int j = 0; j < pixels; ++j) plane[j] *= window[j];
                            for (int y = 0; y < bh; ++y)
                            {
                                int yr = y + (by + 1) * bh + offsy;
                                for (int x = 0; x < bw; ++x)
                                {
                                    int xr = x + (bx + 1) * bw + offsx;
                                    result[yr * resultStride + xr] += (block[y * bw + x] + plane[y * bw + x]) * window[y * bw + x];
                                }
                            }
                        }
            DitherAndQuantize(result, resultStride, denoised[c]!, w, h, stride[c], subW, subH, blockSize, norm);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Entry points
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>av1_estimate_noise_from_single_plane (edge threshold 16 as in the all-intra path): Laplacian noise
    /// estimate over smooth pixels, normalised to 8-bit; -1 when fewer than 16 smooth pixels.</summary>
    internal static double EstimateNoiseLevel(ushort[] y, int w, int h, int stride, int bitDepth, int edgeThresh = 16)
    {
        long accum = 0;
        int count = 0;
        int sh = bitDepth - 8, rnd = sh > 0 ? 1 << (sh - 1) : 0;
        for (int i = 1; i < h - 1; ++i)
            for (int j = 1; j < w - 1; ++j)
            {
                int c = i * stride + j;
                int m00 = y[c - stride - 1], m01 = y[c - stride], m02 = y[c - stride + 1];
                int m10 = y[c - 1], m11 = y[c], m12 = y[c + 1];
                int m20 = y[c + stride - 1], m21 = y[c + stride], m22 = y[c + stride + 1];
                int gx = (m00 - m02) + (m20 - m22) + 2 * (m10 - m12);
                int gy = (m00 - m20) + (m02 - m22) + 2 * (m01 - m21);
                int ga = (Math.Abs(gx) + Math.Abs(gy) + rnd) >> sh;
                if (ga < edgeThresh)
                {
                    int v = 4 * m11 - 2 * (m01 + m21 + m10 + m12) + (m00 + m02 + m20 + m22);
                    accum += (Math.Abs(v) + rnd) >> sh;
                    ++count;
                }
            }
        const double sqrtPiBy2 = 1.25331413732;
        return count < 16 ? -1.0 : (double)accum / (6 * count) * sqrtPiBy2;
    }

    /// <summary>The noise level libaom uses in all-intra mode (AOM_USAGE_ALL_INTRA, as libavif encodes stills) whenever
    /// denoising is enabled: the requested level is replaced by the source's estimated luma noise, minus 0.1, plus
    /// 0.5 when positive, capped at 5 (av1_receive_raw_frame).</summary>
    internal static float AllIntraNoiseLevel(ushort[] y, int w, int h, int bitDepth)
    {
        double yNoise = EstimateNoiseLevel(y, w, h, w, bitDepth, 16);
        float level = (float)(yNoise - 0.1);
        level = (float)AMax(0.0, level);
        if (level > 0.0) level += 0.5f;
        level = (float)AMin(5.0, level);
        return level;
    }

    /// <summary>
    /// aom_denoise_and_model_run for a still image: flat blocks, Wiener denoise (block size <paramref name="blockSize"/>,
    /// flat noise PSD from <paramref name="noiseLevel"/>), AR noise model and grain fit. Planes are tightly packed
    /// (chroma (w+ssX)>>ssX wide); chroma null for monochrome. Returns null when no estimate is possible (too few flat
    /// blocks, or 4:2:2, which libaom's denoiser does not handle). <paramref name="denoised"/> receives the denoised
    /// planes (copies; only filled when an estimate exists).
    /// </summary>
    /// <summary>Why the last <see cref="DenoiseAndModel"/> call on this thread produced no estimate (diagnostics).</summary>
    [ThreadStatic] internal static string? LastStatus;

    internal static AvifFilmGrain? DenoiseAndModel(ushort[] yP, ushort[]? uP, ushort[]? vP, int w, int h, int ssX, int ssY,
        int bitDepth, float noiseLevel, int blockSize, out ushort[]?[] denoised)
    {
        denoised = [null, null, null];
        LastStatus = null;
        bool mono = uP == null || vP == null;
        if (!mono && ssX != ssY) { LastStatus = "4:2:2 not supported by the denoiser"; return null; }   // aom_wiener_denoise_2d: "doesn't handle different chroma subsampling"
        int cw = (w + ssX) >> ssX;
        ushort[]?[] data = [yP, mono ? null : uP, mono ? null : vP];
        int[] stride = [w, cw, cw];
        var dn = new ushort[]?[] { (ushort[])yP.Clone(), mono ? null : (ushort[])uP!.Clone(), mono ? null : (ushort[])vP!.Clone() };

        int numBlocksW = (w + blockSize - 1) / blockSize, numBlocksH = (h + blockSize - 1) / blockSize;
        var flat = new byte[numBlocksW * numBlocksH];
        new FlatBlockFinder(blockSize, bitDepth).Run(yP, w, h, w, flat);

        float yLevel = PsdDefaultValue(blockSize, noiseLevel);
        float uvLevel = PsdDefaultValue(blockSize >> ssX, noiseLevel);
        var psd = new float[3][];
        for (int i = 0; i < 3; i++)
        {
            psd[i] = new float[blockSize * blockSize];
            Array.Fill(psd[i], i == 0 ? yLevel : uvLevel);
        }
        WienerDenoise2d(data, dn, w, h, stride, ssX, ssY, psd, blockSize, bitDepth);

        var model = new NoiseModel(bitDepth);
        if (!ModelUpdate(model, data, dn, w, h, stride, ssX, ssY, flat, blockSize)) return null;
        var grain = GrainParameters(model);
        grain.RandomSeed = 7391;   // libaom: a zero seed becomes 7391
        denoised = dn;
        return grain;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 1-D FFT kernels (generated from libaom aom_dsp/fft_common.h GEN_FFT_* / GEN_IFFT_*)
    // ---------------------------------------------------------------------------------------------------------------

    private static void Fft1d2(float[] input, int io, float[] output, int oo, int stride)
    {
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        output[oo + 0 * stride] = i0 + i1;
        output[oo + 1 * stride] = i0 - i1;
    }

    private static void Ifft1d2(float[] input, int io, float[] output, int oo, int stride)
    {
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        output[oo + 0 * stride] = i0 + i1;
        output[oo + 1 * stride] = i0 - i1;
    }

    private static void Fft1d4(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float w0 = (i0 + i2);
        float w1 = (i0 - i2);
        float w2 = (i1 + i3);
        float w3 = (i1 - i3);
        output[oo + 0 * stride] = (w0 + w2);
        output[oo + 1 * stride] = w1;
        output[oo + 2 * stride] = (w0 - w2);
        output[oo + 3 * stride] = (kWeight0 - w3);
    }

    private static void Ifft1d4(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float w2 = (i0 + i2);
        float w3 = (i0 - i2);
        float w4_0 = (i1 + i1);
        float w4_1 = (i3 - i3);
        float w5_0 = (i1 - i1);
        float w5_1 = ((kWeight0 - i3) - i3);
        output[oo + 0 * stride] = (w2 + w4_0);
        output[oo + 1 * stride] = (w3 + w5_1);
        output[oo + 2 * stride] = (w2 - w4_0);
        output[oo + 3 * stride] = (w3 - w5_1);
    }

    private static void Fft1d8(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float kWeight2 = 0.707107f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float i4 = input[io + 4 * stride];
        float i5 = input[io + 5 * stride];
        float i6 = input[io + 6 * stride];
        float i7 = input[io + 7 * stride];
        float w0 = (i0 + i4);
        float w1 = (i0 - i4);
        float w2 = (i2 + i6);
        float w3 = (i2 - i6);
        float w4 = (w0 + w2);
        float w5 = (w0 - w2);
        float w7 = (i1 + i5);
        float w8 = (i1 - i5);
        float w9 = (i3 + i7);
        float w10 = (i3 - i7);
        float w11 = (w7 + w9);
        float w12 = (w7 - w9);
        output[oo + 0 * stride] = (w4 + w11);
        output[oo + 1 * stride] = (w1 + (kWeight2 * (w8 - w10)));
        output[oo + 2 * stride] = w5;
        output[oo + 3 * stride] = (w1 - (kWeight2 * (w8 - w10)));
        output[oo + 4 * stride] = (w4 - w11);
        output[oo + 5 * stride] = ((kWeight0 - w3) - (kWeight2 * (w10 + w8)));
        output[oo + 6 * stride] = (kWeight0 - w12);
        output[oo + 7 * stride] = (w3 - (kWeight2 * (w10 + w8)));
    }

    private static void Ifft1d8(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float kWeight2 = 0.707107f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float i4 = input[io + 4 * stride];
        float i5 = input[io + 5 * stride];
        float i6 = input[io + 6 * stride];
        float i7 = input[io + 7 * stride];
        float w6 = (i0 + i4);
        float w7 = (i0 - i4);
        float w8_0 = (i2 + i2);
        float w8_1 = (i6 - i6);
        float w9_0 = (i2 - i2);
        float w9_1 = ((kWeight0 - i6) - i6);
        float w10_0 = (w6 + w8_0);
        float w10_1 = w8_1;
        float w11_0 = (w6 - w8_0);
        float w11_1 = (kWeight0 - w8_1);
        float w12_0 = (w7 + w9_1);
        float w12_1 = (kWeight0 - w9_0);
        float w13_0 = (w7 - w9_1);
        float w13_1 = w9_0;
        float w14_0 = (i1 + i3);
        float w14_1 = (i7 - i5);
        float w15_0 = (i1 - i3);
        float w15_1 = ((kWeight0 - i5) - i7);
        float w16_0 = (i3 + i1);
        float w16_1 = (i5 - i7);
        float w17_0 = (i3 - i1);
        float w17_1 = ((kWeight0 - i7) - i5);
        float w18_0 = (w14_0 + w16_0);
        float w18_1 = (w14_1 + w16_1);
        float w19_0 = (w14_0 - w16_0);
        float w19_1 = (w14_1 - w16_1);
        float w20_0 = (w15_0 + w17_1);
        float w20_1 = (w15_1 - w17_0);
        float w21_0 = (w15_0 - w17_1);
        float w21_1 = (w15_1 + w17_0);
        output[oo + 0 * stride] = (w10_0 + w18_0);
        output[oo + 1 * stride] = (w12_0 + (kWeight2 * (w20_0 + w20_1)));
        output[oo + 2 * stride] = (w11_0 + w19_1);
        output[oo + 3 * stride] = (w13_0 - (kWeight2 * (w21_0 - w21_1)));
        output[oo + 4 * stride] = (w10_0 - w18_0);
        output[oo + 5 * stride] = (w12_0 + ((kWeight0 - (kWeight2 * w20_0)) - (kWeight2 * w20_1)));
        output[oo + 6 * stride] = (w11_0 - w19_1);
        output[oo + 7 * stride] = (w13_0 + (kWeight2 * (w21_0 - w21_1)));
    }

    private static void Fft1d16(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float kWeight2 = 0.707107f;
        float kWeight3 = 0.92388f;
        float kWeight4 = 0.382683f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float i4 = input[io + 4 * stride];
        float i5 = input[io + 5 * stride];
        float i6 = input[io + 6 * stride];
        float i7 = input[io + 7 * stride];
        float i8 = input[io + 8 * stride];
        float i9 = input[io + 9 * stride];
        float i10 = input[io + 10 * stride];
        float i11 = input[io + 11 * stride];
        float i12 = input[io + 12 * stride];
        float i13 = input[io + 13 * stride];
        float i14 = input[io + 14 * stride];
        float i15 = input[io + 15 * stride];
        float w0 = (i0 + i8);
        float w1 = (i0 - i8);
        float w2 = (i4 + i12);
        float w3 = (i4 - i12);
        float w4 = (w0 + w2);
        float w5 = (w0 - w2);
        float w7 = (i2 + i10);
        float w8 = (i2 - i10);
        float w9 = (i6 + i14);
        float w10 = (i6 - i14);
        float w11 = (w7 + w9);
        float w12 = (w7 - w9);
        float w14 = (w4 + w11);
        float w15 = (w4 - w11);
        float w16_0 = (w1 + (kWeight2 * (w8 - w10)));
        float w16_1 = ((kWeight0 - w3) - (kWeight2 * (w10 + w8)));
        float w18_0 = (w1 - (kWeight2 * (w8 - w10)));
        float w18_1 = (w3 - (kWeight2 * (w10 + w8)));
        float w19 = (i1 + i9);
        float w20 = (i1 - i9);
        float w21 = (i5 + i13);
        float w22 = (i5 - i13);
        float w23 = (w19 + w21);
        float w24 = (w19 - w21);
        float w26 = (i3 + i11);
        float w27 = (i3 - i11);
        float w28 = (i7 + i15);
        float w29 = (i7 - i15);
        float w30 = (w26 + w28);
        float w31 = (w26 - w28);
        float w33 = (w23 + w30);
        float w34 = (w23 - w30);
        float w35_0 = (w20 + (kWeight2 * (w27 - w29)));
        float w35_1 = ((kWeight0 - w22) - (kWeight2 * (w29 + w27)));
        float w37_0 = (w20 - (kWeight2 * (w27 - w29)));
        float w37_1 = (w22 - (kWeight2 * (w29 + w27)));
        output[oo + 0 * stride] = (w14 + w33);
        output[oo + 1 * stride] = (w16_0 + ((kWeight3 * w35_0) + (kWeight4 * w35_1)));
        output[oo + 2 * stride] = (w5 + (kWeight2 * (w24 - w31)));
        output[oo + 3 * stride] = (w18_0 + ((kWeight4 * w37_0) + (kWeight3 * w37_1)));
        output[oo + 4 * stride] = w15;
        output[oo + 5 * stride] = (w18_0 + ((kWeight0 - (kWeight4 * w37_0)) - (kWeight3 * w37_1)));
        output[oo + 6 * stride] = (w5 - (kWeight2 * (w24 - w31)));
        output[oo + 7 * stride] = (w16_0 + ((kWeight0 - (kWeight3 * w35_0)) - (kWeight4 * w35_1)));
        output[oo + 8 * stride] = (w14 - w33);
        output[oo + 9 * stride] = (w16_1 + ((kWeight3 * w35_1) - (kWeight4 * w35_0)));
        output[oo + 10 * stride] = ((kWeight0 - w12) - (kWeight2 * (w31 + w24)));
        output[oo + 11 * stride] = (w18_1 + ((kWeight4 * w37_1) - (kWeight3 * w37_0)));
        output[oo + 12 * stride] = (kWeight0 - w34);
        output[oo + 13 * stride] = ((kWeight0 - w18_1) - ((kWeight3 * w37_0) - (kWeight4 * w37_1)));
        output[oo + 14 * stride] = (w12 - (kWeight2 * (w31 + w24)));
        output[oo + 15 * stride] = ((kWeight0 - w16_1) - ((kWeight4 * w35_0) - (kWeight3 * w35_1)));
    }

    private static void Ifft1d16(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float kWeight2 = 0.707107f;
        float kWeight3 = 0.92388f;
        float kWeight4 = 0.382683f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float i4 = input[io + 4 * stride];
        float i5 = input[io + 5 * stride];
        float i6 = input[io + 6 * stride];
        float i7 = input[io + 7 * stride];
        float i8 = input[io + 8 * stride];
        float i9 = input[io + 9 * stride];
        float i10 = input[io + 10 * stride];
        float i11 = input[io + 11 * stride];
        float i12 = input[io + 12 * stride];
        float i13 = input[io + 13 * stride];
        float i14 = input[io + 14 * stride];
        float i15 = input[io + 15 * stride];
        float w14 = (i0 + i8);
        float w15 = (i0 - i8);
        float w16_0 = (i4 + i4);
        float w16_1 = (i12 - i12);
        float w17_0 = (i4 - i4);
        float w17_1 = ((kWeight0 - i12) - i12);
        float w18_0 = (w14 + w16_0);
        float w18_1 = w16_1;
        float w19_0 = (w14 - w16_0);
        float w19_1 = (kWeight0 - w16_1);
        float w20_0 = (w15 + w17_1);
        float w20_1 = (kWeight0 - w17_0);
        float w21_0 = (w15 - w17_1);
        float w21_1 = w17_0;
        float w22_0 = (i2 + i6);
        float w22_1 = (i14 - i10);
        float w23_0 = (i2 - i6);
        float w23_1 = ((kWeight0 - i10) - i14);
        float w24_0 = (i6 + i2);
        float w24_1 = (i10 - i14);
        float w25_0 = (i6 - i2);
        float w25_1 = ((kWeight0 - i14) - i10);
        float w26_0 = (w22_0 + w24_0);
        float w26_1 = (w22_1 + w24_1);
        float w27_0 = (w22_0 - w24_0);
        float w27_1 = (w22_1 - w24_1);
        float w28_0 = (w23_0 + w25_1);
        float w28_1 = (w23_1 - w25_0);
        float w29_0 = (w23_0 - w25_1);
        float w29_1 = (w23_1 + w25_0);
        float w30_0 = (w18_0 + w26_0);
        float w30_1 = (w18_1 + w26_1);
        float w31_0 = (w18_0 - w26_0);
        float w31_1 = (w18_1 - w26_1);
        float w32_0 = (w20_0 + (kWeight2 * (w28_0 + w28_1)));
        float w32_1 = (w20_1 + (kWeight2 * (w28_1 - w28_0)));
        float w33_0 = (w20_0 + ((kWeight0 - (kWeight2 * w28_0)) - (kWeight2 * w28_1)));
        float w33_1 = (w20_1 + (kWeight2 * (w28_0 - w28_1)));
        float w34_0 = (w19_0 + w27_1);
        float w34_1 = (w19_1 - w27_0);
        float w35_0 = (w19_0 - w27_1);
        float w35_1 = (w19_1 + w27_0);
        float w36_0 = (w21_0 - (kWeight2 * (w29_0 - w29_1)));
        float w36_1 = (w21_1 - (kWeight2 * (w29_1 + w29_0)));
        float w37_0 = (w21_0 + (kWeight2 * (w29_0 - w29_1)));
        float w37_1 = (w21_1 + (kWeight2 * (w29_1 + w29_0)));
        float w38_0 = (i1 + i7);
        float w38_1 = (i15 - i9);
        float w39_0 = (i1 - i7);
        float w39_1 = ((kWeight0 - i9) - i15);
        float w40_0 = (i5 + i3);
        float w40_1 = (i11 - i13);
        float w41_0 = (i5 - i3);
        float w41_1 = ((kWeight0 - i13) - i11);
        float w42_0 = (w38_0 + w40_0);
        float w42_1 = (w38_1 + w40_1);
        float w43_0 = (w38_0 - w40_0);
        float w43_1 = (w38_1 - w40_1);
        float w44_0 = (w39_0 + w41_1);
        float w44_1 = (w39_1 - w41_0);
        float w45_0 = (w39_0 - w41_1);
        float w45_1 = (w39_1 + w41_0);
        float w46_0 = (i3 + i5);
        float w46_1 = (i13 - i11);
        float w47_0 = (i3 - i5);
        float w47_1 = ((kWeight0 - i11) - i13);
        float w48_0 = (i7 + i1);
        float w48_1 = (i9 - i15);
        float w49_0 = (i7 - i1);
        float w49_1 = ((kWeight0 - i15) - i9);
        float w50_0 = (w46_0 + w48_0);
        float w50_1 = (w46_1 + w48_1);
        float w51_0 = (w46_0 - w48_0);
        float w51_1 = (w46_1 - w48_1);
        float w52_0 = (w47_0 + w49_1);
        float w52_1 = (w47_1 - w49_0);
        float w53_0 = (w47_0 - w49_1);
        float w53_1 = (w47_1 + w49_0);
        float w54_0 = (w42_0 + w50_0);
        float w54_1 = (w42_1 + w50_1);
        float w55_0 = (w42_0 - w50_0);
        float w55_1 = (w42_1 - w50_1);
        float w56_0 = (w44_0 + (kWeight2 * (w52_0 + w52_1)));
        float w56_1 = (w44_1 + (kWeight2 * (w52_1 - w52_0)));
        float w57_0 = (w44_0 + ((kWeight0 - (kWeight2 * w52_0)) - (kWeight2 * w52_1)));
        float w57_1 = (w44_1 + (kWeight2 * (w52_0 - w52_1)));
        float w58_0 = (w43_0 + w51_1);
        float w58_1 = (w43_1 - w51_0);
        float w59_0 = (w43_0 - w51_1);
        float w59_1 = (w43_1 + w51_0);
        float w60_0 = (w45_0 - (kWeight2 * (w53_0 - w53_1)));
        float w60_1 = (w45_1 - (kWeight2 * (w53_1 + w53_0)));
        float w61_0 = (w45_0 + (kWeight2 * (w53_0 - w53_1)));
        float w61_1 = (w45_1 + (kWeight2 * (w53_1 + w53_0)));
        output[oo + 0 * stride] = (w30_0 + w54_0);
        output[oo + 1 * stride] = (w32_0 + ((kWeight3 * w56_0) + (kWeight4 * w56_1)));
        output[oo + 2 * stride] = (w34_0 + (kWeight2 * (w58_0 + w58_1)));
        output[oo + 3 * stride] = (w36_0 + ((kWeight4 * w60_0) + (kWeight3 * w60_1)));
        output[oo + 4 * stride] = (w31_0 + w55_1);
        output[oo + 5 * stride] = (w33_0 - ((kWeight4 * w57_0) - (kWeight3 * w57_1)));
        output[oo + 6 * stride] = (w35_0 - (kWeight2 * (w59_0 - w59_1)));
        output[oo + 7 * stride] = (w37_0 - ((kWeight3 * w61_0) - (kWeight4 * w61_1)));
        output[oo + 8 * stride] = (w30_0 - w54_0);
        output[oo + 9 * stride] = (w32_0 + ((kWeight0 - (kWeight3 * w56_0)) - (kWeight4 * w56_1)));
        output[oo + 10 * stride] = (w34_0 + ((kWeight0 - (kWeight2 * w58_0)) - (kWeight2 * w58_1)));
        output[oo + 11 * stride] = (w36_0 + ((kWeight0 - (kWeight4 * w60_0)) - (kWeight3 * w60_1)));
        output[oo + 12 * stride] = (w31_0 - w55_1);
        output[oo + 13 * stride] = (w33_0 + ((kWeight4 * w57_0) - (kWeight3 * w57_1)));
        output[oo + 14 * stride] = (w35_0 + (kWeight2 * (w59_0 - w59_1)));
        output[oo + 15 * stride] = (w37_0 + ((kWeight3 * w61_0) - (kWeight4 * w61_1)));
    }

    private static void Fft1d32(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float kWeight2 = 0.707107f;
        float kWeight3 = 0.92388f;
        float kWeight4 = 0.382683f;
        float kWeight5 = 0.980785f;
        float kWeight6 = 0.19509f;
        float kWeight7 = 0.83147f;
        float kWeight8 = 0.55557f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float i4 = input[io + 4 * stride];
        float i5 = input[io + 5 * stride];
        float i6 = input[io + 6 * stride];
        float i7 = input[io + 7 * stride];
        float i8 = input[io + 8 * stride];
        float i9 = input[io + 9 * stride];
        float i10 = input[io + 10 * stride];
        float i11 = input[io + 11 * stride];
        float i12 = input[io + 12 * stride];
        float i13 = input[io + 13 * stride];
        float i14 = input[io + 14 * stride];
        float i15 = input[io + 15 * stride];
        float i16 = input[io + 16 * stride];
        float i17 = input[io + 17 * stride];
        float i18 = input[io + 18 * stride];
        float i19 = input[io + 19 * stride];
        float i20 = input[io + 20 * stride];
        float i21 = input[io + 21 * stride];
        float i22 = input[io + 22 * stride];
        float i23 = input[io + 23 * stride];
        float i24 = input[io + 24 * stride];
        float i25 = input[io + 25 * stride];
        float i26 = input[io + 26 * stride];
        float i27 = input[io + 27 * stride];
        float i28 = input[io + 28 * stride];
        float i29 = input[io + 29 * stride];
        float i30 = input[io + 30 * stride];
        float i31 = input[io + 31 * stride];
        float w0 = (i0 + i16);
        float w1 = (i0 - i16);
        float w2 = (i8 + i24);
        float w3 = (i8 - i24);
        float w4 = (w0 + w2);
        float w5 = (w0 - w2);
        float w7 = (i4 + i20);
        float w8 = (i4 - i20);
        float w9 = (i12 + i28);
        float w10 = (i12 - i28);
        float w11 = (w7 + w9);
        float w12 = (w7 - w9);
        float w14 = (w4 + w11);
        float w15 = (w4 - w11);
        float w16_0 = (w1 + (kWeight2 * (w8 - w10)));
        float w16_1 = ((kWeight0 - w3) - (kWeight2 * (w10 + w8)));
        float w18_0 = (w1 - (kWeight2 * (w8 - w10)));
        float w18_1 = (w3 - (kWeight2 * (w10 + w8)));
        float w19 = (i2 + i18);
        float w20 = (i2 - i18);
        float w21 = (i10 + i26);
        float w22 = (i10 - i26);
        float w23 = (w19 + w21);
        float w24 = (w19 - w21);
        float w26 = (i6 + i22);
        float w27 = (i6 - i22);
        float w28 = (i14 + i30);
        float w29 = (i14 - i30);
        float w30 = (w26 + w28);
        float w31 = (w26 - w28);
        float w33 = (w23 + w30);
        float w34 = (w23 - w30);
        float w35_0 = (w20 + (kWeight2 * (w27 - w29)));
        float w35_1 = ((kWeight0 - w22) - (kWeight2 * (w29 + w27)));
        float w37_0 = (w20 - (kWeight2 * (w27 - w29)));
        float w37_1 = (w22 - (kWeight2 * (w29 + w27)));
        float w38 = (w14 + w33);
        float w39 = (w14 - w33);
        float w40_0 = (w16_0 + ((kWeight3 * w35_0) + (kWeight4 * w35_1)));
        float w40_1 = (w16_1 + ((kWeight3 * w35_1) - (kWeight4 * w35_0)));
        float w41_0 = (w5 + (kWeight2 * (w24 - w31)));
        float w41_1 = ((kWeight0 - w12) - (kWeight2 * (w31 + w24)));
        float w42_0 = (w18_0 + ((kWeight4 * w37_0) + (kWeight3 * w37_1)));
        float w42_1 = (w18_1 + ((kWeight4 * w37_1) - (kWeight3 * w37_0)));
        float w44_0 = (w18_0 + ((kWeight0 - (kWeight4 * w37_0)) - (kWeight3 * w37_1)));
        float w44_1 = ((kWeight0 - w18_1) - ((kWeight3 * w37_0) - (kWeight4 * w37_1)));
        float w45_0 = (w5 - (kWeight2 * (w24 - w31)));
        float w45_1 = (w12 - (kWeight2 * (w31 + w24)));
        float w46_0 = (w16_0 + ((kWeight0 - (kWeight3 * w35_0)) - (kWeight4 * w35_1)));
        float w46_1 = ((kWeight0 - w16_1) - ((kWeight4 * w35_0) - (kWeight3 * w35_1)));
        float w47 = (i1 + i17);
        float w48 = (i1 - i17);
        float w49 = (i9 + i25);
        float w50 = (i9 - i25);
        float w51 = (w47 + w49);
        float w52 = (w47 - w49);
        float w54 = (i5 + i21);
        float w55 = (i5 - i21);
        float w56 = (i13 + i29);
        float w57 = (i13 - i29);
        float w58 = (w54 + w56);
        float w59 = (w54 - w56);
        float w61 = (w51 + w58);
        float w62 = (w51 - w58);
        float w63_0 = (w48 + (kWeight2 * (w55 - w57)));
        float w63_1 = ((kWeight0 - w50) - (kWeight2 * (w57 + w55)));
        float w65_0 = (w48 - (kWeight2 * (w55 - w57)));
        float w65_1 = (w50 - (kWeight2 * (w57 + w55)));
        float w66 = (i3 + i19);
        float w67 = (i3 - i19);
        float w68 = (i11 + i27);
        float w69 = (i11 - i27);
        float w70 = (w66 + w68);
        float w71 = (w66 - w68);
        float w73 = (i7 + i23);
        float w74 = (i7 - i23);
        float w75 = (i15 + i31);
        float w76 = (i15 - i31);
        float w77 = (w73 + w75);
        float w78 = (w73 - w75);
        float w80 = (w70 + w77);
        float w81 = (w70 - w77);
        float w82_0 = (w67 + (kWeight2 * (w74 - w76)));
        float w82_1 = ((kWeight0 - w69) - (kWeight2 * (w76 + w74)));
        float w84_0 = (w67 - (kWeight2 * (w74 - w76)));
        float w84_1 = (w69 - (kWeight2 * (w76 + w74)));
        float w85 = (w61 + w80);
        float w86 = (w61 - w80);
        float w87_0 = (w63_0 + ((kWeight3 * w82_0) + (kWeight4 * w82_1)));
        float w87_1 = (w63_1 + ((kWeight3 * w82_1) - (kWeight4 * w82_0)));
        float w88_0 = (w52 + (kWeight2 * (w71 - w78)));
        float w88_1 = ((kWeight0 - w59) - (kWeight2 * (w78 + w71)));
        float w89_0 = (w65_0 + ((kWeight4 * w84_0) + (kWeight3 * w84_1)));
        float w89_1 = (w65_1 + ((kWeight4 * w84_1) - (kWeight3 * w84_0)));
        float w91_0 = (w65_0 + ((kWeight0 - (kWeight4 * w84_0)) - (kWeight3 * w84_1)));
        float w91_1 = ((kWeight0 - w65_1) - ((kWeight3 * w84_0) - (kWeight4 * w84_1)));
        float w92_0 = (w52 - (kWeight2 * (w71 - w78)));
        float w92_1 = (w59 - (kWeight2 * (w78 + w71)));
        float w93_0 = (w63_0 + ((kWeight0 - (kWeight3 * w82_0)) - (kWeight4 * w82_1)));
        float w93_1 = ((kWeight0 - w63_1) - ((kWeight4 * w82_0) - (kWeight3 * w82_1)));
        output[oo + 0 * stride] = (w38 + w85);
        output[oo + 1 * stride] = (w40_0 + ((kWeight5 * w87_0) + (kWeight6 * w87_1)));
        output[oo + 2 * stride] = (w41_0 + ((kWeight3 * w88_0) + (kWeight4 * w88_1)));
        output[oo + 3 * stride] = (w42_0 + ((kWeight7 * w89_0) + (kWeight8 * w89_1)));
        output[oo + 4 * stride] = (w15 + (kWeight2 * (w62 - w81)));
        output[oo + 5 * stride] = (w44_0 + ((kWeight8 * w91_0) + (kWeight7 * w91_1)));
        output[oo + 6 * stride] = (w45_0 + ((kWeight4 * w92_0) + (kWeight3 * w92_1)));
        output[oo + 7 * stride] = (w46_0 + ((kWeight6 * w93_0) + (kWeight5 * w93_1)));
        output[oo + 8 * stride] = w39;
        output[oo + 9 * stride] = (w46_0 + ((kWeight0 - (kWeight6 * w93_0)) - (kWeight5 * w93_1)));
        output[oo + 10 * stride] = (w45_0 + ((kWeight0 - (kWeight4 * w92_0)) - (kWeight3 * w92_1)));
        output[oo + 11 * stride] = (w44_0 + ((kWeight0 - (kWeight8 * w91_0)) - (kWeight7 * w91_1)));
        output[oo + 12 * stride] = (w15 - (kWeight2 * (w62 - w81)));
        output[oo + 13 * stride] = (w42_0 + ((kWeight0 - (kWeight7 * w89_0)) - (kWeight8 * w89_1)));
        output[oo + 14 * stride] = (w41_0 + ((kWeight0 - (kWeight3 * w88_0)) - (kWeight4 * w88_1)));
        output[oo + 15 * stride] = (w40_0 + ((kWeight0 - (kWeight5 * w87_0)) - (kWeight6 * w87_1)));
        output[oo + 16 * stride] = (w38 - w85);
        output[oo + 17 * stride] = (w40_1 + ((kWeight5 * w87_1) - (kWeight6 * w87_0)));
        output[oo + 18 * stride] = (w41_1 + ((kWeight3 * w88_1) - (kWeight4 * w88_0)));
        output[oo + 19 * stride] = (w42_1 + ((kWeight7 * w89_1) - (kWeight8 * w89_0)));
        output[oo + 20 * stride] = ((kWeight0 - w34) - (kWeight2 * (w81 + w62)));
        output[oo + 21 * stride] = (w44_1 + ((kWeight8 * w91_1) - (kWeight7 * w91_0)));
        output[oo + 22 * stride] = (w45_1 + ((kWeight4 * w92_1) - (kWeight3 * w92_0)));
        output[oo + 23 * stride] = (w46_1 + ((kWeight6 * w93_1) - (kWeight5 * w93_0)));
        output[oo + 24 * stride] = (kWeight0 - w86);
        output[oo + 25 * stride] = ((kWeight0 - w46_1) - ((kWeight5 * w93_0) - (kWeight6 * w93_1)));
        output[oo + 26 * stride] = ((kWeight0 - w45_1) - ((kWeight3 * w92_0) - (kWeight4 * w92_1)));
        output[oo + 27 * stride] = ((kWeight0 - w44_1) - ((kWeight7 * w91_0) - (kWeight8 * w91_1)));
        output[oo + 28 * stride] = (w34 - (kWeight2 * (w81 + w62)));
        output[oo + 29 * stride] = ((kWeight0 - w42_1) - ((kWeight8 * w89_0) - (kWeight7 * w89_1)));
        output[oo + 30 * stride] = ((kWeight0 - w41_1) - ((kWeight4 * w88_0) - (kWeight3 * w88_1)));
        output[oo + 31 * stride] = ((kWeight0 - w40_1) - ((kWeight6 * w87_0) - (kWeight5 * w87_1)));
    }

    private static void Ifft1d32(float[] input, int io, float[] output, int oo, int stride)
    {
        float kWeight0 = 0.0f;
        float kWeight2 = 0.707107f;
        float kWeight3 = 0.92388f;
        float kWeight4 = 0.382683f;
        float kWeight5 = 0.980785f;
        float kWeight6 = 0.19509f;
        float kWeight7 = 0.83147f;
        float kWeight8 = 0.55557f;
        float i0 = input[io + 0 * stride];
        float i1 = input[io + 1 * stride];
        float i2 = input[io + 2 * stride];
        float i3 = input[io + 3 * stride];
        float i4 = input[io + 4 * stride];
        float i5 = input[io + 5 * stride];
        float i6 = input[io + 6 * stride];
        float i7 = input[io + 7 * stride];
        float i8 = input[io + 8 * stride];
        float i9 = input[io + 9 * stride];
        float i10 = input[io + 10 * stride];
        float i11 = input[io + 11 * stride];
        float i12 = input[io + 12 * stride];
        float i13 = input[io + 13 * stride];
        float i14 = input[io + 14 * stride];
        float i15 = input[io + 15 * stride];
        float i16 = input[io + 16 * stride];
        float i17 = input[io + 17 * stride];
        float i18 = input[io + 18 * stride];
        float i19 = input[io + 19 * stride];
        float i20 = input[io + 20 * stride];
        float i21 = input[io + 21 * stride];
        float i22 = input[io + 22 * stride];
        float i23 = input[io + 23 * stride];
        float i24 = input[io + 24 * stride];
        float i25 = input[io + 25 * stride];
        float i26 = input[io + 26 * stride];
        float i27 = input[io + 27 * stride];
        float i28 = input[io + 28 * stride];
        float i29 = input[io + 29 * stride];
        float i30 = input[io + 30 * stride];
        float i31 = input[io + 31 * stride];
        float w30 = (i0 + i16);
        float w31 = (i0 - i16);
        float w32_0 = (i8 + i8);
        float w32_1 = (i24 - i24);
        float w33_0 = (i8 - i8);
        float w33_1 = ((kWeight0 - i24) - i24);
        float w34_0 = (w30 + w32_0);
        float w34_1 = w32_1;
        float w35_0 = (w30 - w32_0);
        float w35_1 = (kWeight0 - w32_1);
        float w36_0 = (w31 + w33_1);
        float w36_1 = (kWeight0 - w33_0);
        float w37_0 = (w31 - w33_1);
        float w37_1 = w33_0;
        float w38_0 = (i4 + i12);
        float w38_1 = (i28 - i20);
        float w39_0 = (i4 - i12);
        float w39_1 = ((kWeight0 - i20) - i28);
        float w40_0 = (i12 + i4);
        float w40_1 = (i20 - i28);
        float w41_0 = (i12 - i4);
        float w41_1 = ((kWeight0 - i28) - i20);
        float w42_0 = (w38_0 + w40_0);
        float w42_1 = (w38_1 + w40_1);
        float w43_0 = (w38_0 - w40_0);
        float w43_1 = (w38_1 - w40_1);
        float w44_0 = (w39_0 + w41_1);
        float w44_1 = (w39_1 - w41_0);
        float w45_0 = (w39_0 - w41_1);
        float w45_1 = (w39_1 + w41_0);
        float w46_0 = (w34_0 + w42_0);
        float w46_1 = (w34_1 + w42_1);
        float w47_0 = (w34_0 - w42_0);
        float w47_1 = (w34_1 - w42_1);
        float w48_0 = (w36_0 + (kWeight2 * (w44_0 + w44_1)));
        float w48_1 = (w36_1 + (kWeight2 * (w44_1 - w44_0)));
        float w49_0 = (w36_0 + ((kWeight0 - (kWeight2 * w44_0)) - (kWeight2 * w44_1)));
        float w49_1 = (w36_1 + (kWeight2 * (w44_0 - w44_1)));
        float w50_0 = (w35_0 + w43_1);
        float w50_1 = (w35_1 - w43_0);
        float w51_0 = (w35_0 - w43_1);
        float w51_1 = (w35_1 + w43_0);
        float w52_0 = (w37_0 - (kWeight2 * (w45_0 - w45_1)));
        float w52_1 = (w37_1 - (kWeight2 * (w45_1 + w45_0)));
        float w53_0 = (w37_0 + (kWeight2 * (w45_0 - w45_1)));
        float w53_1 = (w37_1 + (kWeight2 * (w45_1 + w45_0)));
        float w54_0 = (i2 + i14);
        float w54_1 = (i30 - i18);
        float w55_0 = (i2 - i14);
        float w55_1 = ((kWeight0 - i18) - i30);
        float w56_0 = (i10 + i6);
        float w56_1 = (i22 - i26);
        float w57_0 = (i10 - i6);
        float w57_1 = ((kWeight0 - i26) - i22);
        float w58_0 = (w54_0 + w56_0);
        float w58_1 = (w54_1 + w56_1);
        float w59_0 = (w54_0 - w56_0);
        float w59_1 = (w54_1 - w56_1);
        float w60_0 = (w55_0 + w57_1);
        float w60_1 = (w55_1 - w57_0);
        float w61_0 = (w55_0 - w57_1);
        float w61_1 = (w55_1 + w57_0);
        float w62_0 = (i6 + i10);
        float w62_1 = (i26 - i22);
        float w63_0 = (i6 - i10);
        float w63_1 = ((kWeight0 - i22) - i26);
        float w64_0 = (i14 + i2);
        float w64_1 = (i18 - i30);
        float w65_0 = (i14 - i2);
        float w65_1 = ((kWeight0 - i30) - i18);
        float w66_0 = (w62_0 + w64_0);
        float w66_1 = (w62_1 + w64_1);
        float w67_0 = (w62_0 - w64_0);
        float w67_1 = (w62_1 - w64_1);
        float w68_0 = (w63_0 + w65_1);
        float w68_1 = (w63_1 - w65_0);
        float w69_0 = (w63_0 - w65_1);
        float w69_1 = (w63_1 + w65_0);
        float w70_0 = (w58_0 + w66_0);
        float w70_1 = (w58_1 + w66_1);
        float w71_0 = (w58_0 - w66_0);
        float w71_1 = (w58_1 - w66_1);
        float w72_0 = (w60_0 + (kWeight2 * (w68_0 + w68_1)));
        float w72_1 = (w60_1 + (kWeight2 * (w68_1 - w68_0)));
        float w73_0 = (w60_0 + ((kWeight0 - (kWeight2 * w68_0)) - (kWeight2 * w68_1)));
        float w73_1 = (w60_1 + (kWeight2 * (w68_0 - w68_1)));
        float w74_0 = (w59_0 + w67_1);
        float w74_1 = (w59_1 - w67_0);
        float w75_0 = (w59_0 - w67_1);
        float w75_1 = (w59_1 + w67_0);
        float w76_0 = (w61_0 - (kWeight2 * (w69_0 - w69_1)));
        float w76_1 = (w61_1 - (kWeight2 * (w69_1 + w69_0)));
        float w77_0 = (w61_0 + (kWeight2 * (w69_0 - w69_1)));
        float w77_1 = (w61_1 + (kWeight2 * (w69_1 + w69_0)));
        float w78_0 = (w46_0 + w70_0);
        float w78_1 = (w46_1 + w70_1);
        float w79_0 = (w46_0 - w70_0);
        float w79_1 = (w46_1 - w70_1);
        float w80_0 = (w48_0 + ((kWeight3 * w72_0) + (kWeight4 * w72_1)));
        float w80_1 = (w48_1 + ((kWeight3 * w72_1) - (kWeight4 * w72_0)));
        float w81_0 = (w48_0 + ((kWeight0 - (kWeight3 * w72_0)) - (kWeight4 * w72_1)));
        float w81_1 = (w48_1 + ((kWeight4 * w72_0) - (kWeight3 * w72_1)));
        float w82_0 = (w50_0 + (kWeight2 * (w74_0 + w74_1)));
        float w82_1 = (w50_1 + (kWeight2 * (w74_1 - w74_0)));
        float w83_0 = (w50_0 + ((kWeight0 - (kWeight2 * w74_0)) - (kWeight2 * w74_1)));
        float w83_1 = (w50_1 + (kWeight2 * (w74_0 - w74_1)));
        float w84_0 = (w52_0 + ((kWeight4 * w76_0) + (kWeight3 * w76_1)));
        float w84_1 = (w52_1 + ((kWeight4 * w76_1) - (kWeight3 * w76_0)));
        float w85_0 = (w52_0 + ((kWeight0 - (kWeight4 * w76_0)) - (kWeight3 * w76_1)));
        float w85_1 = (w52_1 + ((kWeight3 * w76_0) - (kWeight4 * w76_1)));
        float w86_0 = (w47_0 + w71_1);
        float w86_1 = (w47_1 - w71_0);
        float w87_0 = (w47_0 - w71_1);
        float w87_1 = (w47_1 + w71_0);
        float w88_0 = (w49_0 - ((kWeight4 * w73_0) - (kWeight3 * w73_1)));
        float w88_1 = (w49_1 + ((kWeight0 - (kWeight4 * w73_1)) - (kWeight3 * w73_0)));
        float w89_0 = (w49_0 + ((kWeight4 * w73_0) - (kWeight3 * w73_1)));
        float w89_1 = (w49_1 + ((kWeight4 * w73_1) + (kWeight3 * w73_0)));
        float w90_0 = (w51_0 - (kWeight2 * (w75_0 - w75_1)));
        float w90_1 = (w51_1 - (kWeight2 * (w75_1 + w75_0)));
        float w91_0 = (w51_0 + (kWeight2 * (w75_0 - w75_1)));
        float w91_1 = (w51_1 + (kWeight2 * (w75_1 + w75_0)));
        float w92_0 = (w53_0 - ((kWeight3 * w77_0) - (kWeight4 * w77_1)));
        float w92_1 = (w53_1 + ((kWeight0 - (kWeight3 * w77_1)) - (kWeight4 * w77_0)));
        float w93_0 = (w53_0 + ((kWeight3 * w77_0) - (kWeight4 * w77_1)));
        float w93_1 = (w53_1 + ((kWeight3 * w77_1) + (kWeight4 * w77_0)));
        float w94_0 = (i1 + i15);
        float w94_1 = (i31 - i17);
        float w95_0 = (i1 - i15);
        float w95_1 = ((kWeight0 - i17) - i31);
        float w96_0 = (i9 + i7);
        float w96_1 = (i23 - i25);
        float w97_0 = (i9 - i7);
        float w97_1 = ((kWeight0 - i25) - i23);
        float w98_0 = (w94_0 + w96_0);
        float w98_1 = (w94_1 + w96_1);
        float w99_0 = (w94_0 - w96_0);
        float w99_1 = (w94_1 - w96_1);
        float w100_0 = (w95_0 + w97_1);
        float w100_1 = (w95_1 - w97_0);
        float w101_0 = (w95_0 - w97_1);
        float w101_1 = (w95_1 + w97_0);
        float w102_0 = (i5 + i11);
        float w102_1 = (i27 - i21);
        float w103_0 = (i5 - i11);
        float w103_1 = ((kWeight0 - i21) - i27);
        float w104_0 = (i13 + i3);
        float w104_1 = (i19 - i29);
        float w105_0 = (i13 - i3);
        float w105_1 = ((kWeight0 - i29) - i19);
        float w106_0 = (w102_0 + w104_0);
        float w106_1 = (w102_1 + w104_1);
        float w107_0 = (w102_0 - w104_0);
        float w107_1 = (w102_1 - w104_1);
        float w108_0 = (w103_0 + w105_1);
        float w108_1 = (w103_1 - w105_0);
        float w109_0 = (w103_0 - w105_1);
        float w109_1 = (w103_1 + w105_0);
        float w110_0 = (w98_0 + w106_0);
        float w110_1 = (w98_1 + w106_1);
        float w111_0 = (w98_0 - w106_0);
        float w111_1 = (w98_1 - w106_1);
        float w112_0 = (w100_0 + (kWeight2 * (w108_0 + w108_1)));
        float w112_1 = (w100_1 + (kWeight2 * (w108_1 - w108_0)));
        float w113_0 = (w100_0 + ((kWeight0 - (kWeight2 * w108_0)) - (kWeight2 * w108_1)));
        float w113_1 = (w100_1 + (kWeight2 * (w108_0 - w108_1)));
        float w114_0 = (w99_0 + w107_1);
        float w114_1 = (w99_1 - w107_0);
        float w115_0 = (w99_0 - w107_1);
        float w115_1 = (w99_1 + w107_0);
        float w116_0 = (w101_0 - (kWeight2 * (w109_0 - w109_1)));
        float w116_1 = (w101_1 - (kWeight2 * (w109_1 + w109_0)));
        float w117_0 = (w101_0 + (kWeight2 * (w109_0 - w109_1)));
        float w117_1 = (w101_1 + (kWeight2 * (w109_1 + w109_0)));
        float w118_0 = (i3 + i13);
        float w118_1 = (i29 - i19);
        float w119_0 = (i3 - i13);
        float w119_1 = ((kWeight0 - i19) - i29);
        float w120_0 = (i11 + i5);
        float w120_1 = (i21 - i27);
        float w121_0 = (i11 - i5);
        float w121_1 = ((kWeight0 - i27) - i21);
        float w122_0 = (w118_0 + w120_0);
        float w122_1 = (w118_1 + w120_1);
        float w123_0 = (w118_0 - w120_0);
        float w123_1 = (w118_1 - w120_1);
        float w124_0 = (w119_0 + w121_1);
        float w124_1 = (w119_1 - w121_0);
        float w125_0 = (w119_0 - w121_1);
        float w125_1 = (w119_1 + w121_0);
        float w126_0 = (i7 + i9);
        float w126_1 = (i25 - i23);
        float w127_0 = (i7 - i9);
        float w127_1 = ((kWeight0 - i23) - i25);
        float w128_0 = (i15 + i1);
        float w128_1 = (i17 - i31);
        float w129_0 = (i15 - i1);
        float w129_1 = ((kWeight0 - i31) - i17);
        float w130_0 = (w126_0 + w128_0);
        float w130_1 = (w126_1 + w128_1);
        float w131_0 = (w126_0 - w128_0);
        float w131_1 = (w126_1 - w128_1);
        float w132_0 = (w127_0 + w129_1);
        float w132_1 = (w127_1 - w129_0);
        float w133_0 = (w127_0 - w129_1);
        float w133_1 = (w127_1 + w129_0);
        float w134_0 = (w122_0 + w130_0);
        float w134_1 = (w122_1 + w130_1);
        float w135_0 = (w122_0 - w130_0);
        float w135_1 = (w122_1 - w130_1);
        float w136_0 = (w124_0 + (kWeight2 * (w132_0 + w132_1)));
        float w136_1 = (w124_1 + (kWeight2 * (w132_1 - w132_0)));
        float w137_0 = (w124_0 + ((kWeight0 - (kWeight2 * w132_0)) - (kWeight2 * w132_1)));
        float w137_1 = (w124_1 + (kWeight2 * (w132_0 - w132_1)));
        float w138_0 = (w123_0 + w131_1);
        float w138_1 = (w123_1 - w131_0);
        float w139_0 = (w123_0 - w131_1);
        float w139_1 = (w123_1 + w131_0);
        float w140_0 = (w125_0 - (kWeight2 * (w133_0 - w133_1)));
        float w140_1 = (w125_1 - (kWeight2 * (w133_1 + w133_0)));
        float w141_0 = (w125_0 + (kWeight2 * (w133_0 - w133_1)));
        float w141_1 = (w125_1 + (kWeight2 * (w133_1 + w133_0)));
        float w142_0 = (w110_0 + w134_0);
        float w142_1 = (w110_1 + w134_1);
        float w143_0 = (w110_0 - w134_0);
        float w143_1 = (w110_1 - w134_1);
        float w144_0 = (w112_0 + ((kWeight3 * w136_0) + (kWeight4 * w136_1)));
        float w144_1 = (w112_1 + ((kWeight3 * w136_1) - (kWeight4 * w136_0)));
        float w145_0 = (w112_0 + ((kWeight0 - (kWeight3 * w136_0)) - (kWeight4 * w136_1)));
        float w145_1 = (w112_1 + ((kWeight4 * w136_0) - (kWeight3 * w136_1)));
        float w146_0 = (w114_0 + (kWeight2 * (w138_0 + w138_1)));
        float w146_1 = (w114_1 + (kWeight2 * (w138_1 - w138_0)));
        float w147_0 = (w114_0 + ((kWeight0 - (kWeight2 * w138_0)) - (kWeight2 * w138_1)));
        float w147_1 = (w114_1 + (kWeight2 * (w138_0 - w138_1)));
        float w148_0 = (w116_0 + ((kWeight4 * w140_0) + (kWeight3 * w140_1)));
        float w148_1 = (w116_1 + ((kWeight4 * w140_1) - (kWeight3 * w140_0)));
        float w149_0 = (w116_0 + ((kWeight0 - (kWeight4 * w140_0)) - (kWeight3 * w140_1)));
        float w149_1 = (w116_1 + ((kWeight3 * w140_0) - (kWeight4 * w140_1)));
        float w150_0 = (w111_0 + w135_1);
        float w150_1 = (w111_1 - w135_0);
        float w151_0 = (w111_0 - w135_1);
        float w151_1 = (w111_1 + w135_0);
        float w152_0 = (w113_0 - ((kWeight4 * w137_0) - (kWeight3 * w137_1)));
        float w152_1 = (w113_1 + ((kWeight0 - (kWeight4 * w137_1)) - (kWeight3 * w137_0)));
        float w153_0 = (w113_0 + ((kWeight4 * w137_0) - (kWeight3 * w137_1)));
        float w153_1 = (w113_1 + ((kWeight4 * w137_1) + (kWeight3 * w137_0)));
        float w154_0 = (w115_0 - (kWeight2 * (w139_0 - w139_1)));
        float w154_1 = (w115_1 - (kWeight2 * (w139_1 + w139_0)));
        float w155_0 = (w115_0 + (kWeight2 * (w139_0 - w139_1)));
        float w155_1 = (w115_1 + (kWeight2 * (w139_1 + w139_0)));
        float w156_0 = (w117_0 - ((kWeight3 * w141_0) - (kWeight4 * w141_1)));
        float w156_1 = (w117_1 + ((kWeight0 - (kWeight3 * w141_1)) - (kWeight4 * w141_0)));
        float w157_0 = (w117_0 + ((kWeight3 * w141_0) - (kWeight4 * w141_1)));
        float w157_1 = (w117_1 + ((kWeight3 * w141_1) + (kWeight4 * w141_0)));
        output[oo + 0 * stride] = (w78_0 + w142_0);
        output[oo + 1 * stride] = (w80_0 + ((kWeight5 * w144_0) + (kWeight6 * w144_1)));
        output[oo + 2 * stride] = (w82_0 + ((kWeight3 * w146_0) + (kWeight4 * w146_1)));
        output[oo + 3 * stride] = (w84_0 + ((kWeight7 * w148_0) + (kWeight8 * w148_1)));
        output[oo + 4 * stride] = (w86_0 + (kWeight2 * (w150_0 + w150_1)));
        output[oo + 5 * stride] = (w88_0 + ((kWeight8 * w152_0) + (kWeight7 * w152_1)));
        output[oo + 6 * stride] = (w90_0 + ((kWeight4 * w154_0) + (kWeight3 * w154_1)));
        output[oo + 7 * stride] = (w92_0 + ((kWeight6 * w156_0) + (kWeight5 * w156_1)));
        output[oo + 8 * stride] = (w79_0 + w143_1);
        output[oo + 9 * stride] = (w81_0 - ((kWeight6 * w145_0) - (kWeight5 * w145_1)));
        output[oo + 10 * stride] = (w83_0 - ((kWeight4 * w147_0) - (kWeight3 * w147_1)));
        output[oo + 11 * stride] = (w85_0 - ((kWeight8 * w149_0) - (kWeight7 * w149_1)));
        output[oo + 12 * stride] = (w87_0 - (kWeight2 * (w151_0 - w151_1)));
        output[oo + 13 * stride] = (w89_0 - ((kWeight7 * w153_0) - (kWeight8 * w153_1)));
        output[oo + 14 * stride] = (w91_0 - ((kWeight3 * w155_0) - (kWeight4 * w155_1)));
        output[oo + 15 * stride] = (w93_0 - ((kWeight5 * w157_0) - (kWeight6 * w157_1)));
        output[oo + 16 * stride] = (w78_0 - w142_0);
        output[oo + 17 * stride] = (w80_0 + ((kWeight0 - (kWeight5 * w144_0)) - (kWeight6 * w144_1)));
        output[oo + 18 * stride] = (w82_0 + ((kWeight0 - (kWeight3 * w146_0)) - (kWeight4 * w146_1)));
        output[oo + 19 * stride] = (w84_0 + ((kWeight0 - (kWeight7 * w148_0)) - (kWeight8 * w148_1)));
        output[oo + 20 * stride] = (w86_0 + ((kWeight0 - (kWeight2 * w150_0)) - (kWeight2 * w150_1)));
        output[oo + 21 * stride] = (w88_0 + ((kWeight0 - (kWeight8 * w152_0)) - (kWeight7 * w152_1)));
        output[oo + 22 * stride] = (w90_0 + ((kWeight0 - (kWeight4 * w154_0)) - (kWeight3 * w154_1)));
        output[oo + 23 * stride] = (w92_0 + ((kWeight0 - (kWeight6 * w156_0)) - (kWeight5 * w156_1)));
        output[oo + 24 * stride] = (w79_0 - w143_1);
        output[oo + 25 * stride] = (w81_0 + ((kWeight6 * w145_0) - (kWeight5 * w145_1)));
        output[oo + 26 * stride] = (w83_0 + ((kWeight4 * w147_0) - (kWeight3 * w147_1)));
        output[oo + 27 * stride] = (w85_0 + ((kWeight8 * w149_0) - (kWeight7 * w149_1)));
        output[oo + 28 * stride] = (w87_0 + (kWeight2 * (w151_0 - w151_1)));
        output[oo + 29 * stride] = (w89_0 + ((kWeight7 * w153_0) - (kWeight8 * w153_1)));
        output[oo + 30 * stride] = (w91_0 + ((kWeight3 * w155_0) - (kWeight4 * w155_1)));
        output[oo + 31 * stride] = (w93_0 + ((kWeight5 * w157_0) - (kWeight6 * w157_1)));
    }
}
