// SharpImage — Virtual pixel helper for edge-handling during filter/transform operations.

using System.Runtime.CompilerServices;

namespace SharpImage.Core;

/// <summary>
/// Maps out-of-bounds coordinates to valid pixel positions using the specified virtual pixel method.
/// </summary>
public static class VirtualPixel
{
    /// <summary>
    /// Maps an (x, y) coordinate that may be outside image bounds to a valid (x, y) coordinate pair,
    /// or returns false if the pixel should use a constant color (background/transparent/etc.).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MapCoordinate(int x, int y, int width, int height,
        VirtualPixelMethod method, out int mappedX, out int mappedY)
    {
        // Fast path: coordinate is in bounds
        if ((uint)x < (uint)width && (uint)y < (uint)height)
        {
            mappedX = x;
            mappedY = y;
            return true;
        }

        switch (method)
        {
            case VirtualPixelMethod.Edge:
                mappedX = Math.Clamp(x, 0, width - 1);
                mappedY = Math.Clamp(y, 0, height - 1);
                return true;

            case VirtualPixelMethod.Mirror:
                mappedX = MirrorIndex(x, width);
                mappedY = MirrorIndex(y, height);
                return true;

            case VirtualPixelMethod.Tile:
                mappedX = ((x % width) + width) % width;
                mappedY = ((y % height) + height) % height;
                return true;

            case VirtualPixelMethod.HorizontalTile:
                mappedX = ((x % width) + width) % width;
                mappedY = Math.Clamp(y, 0, height - 1);
                if ((uint)y >= (uint)height) { mappedX = 0; mappedY = 0; return false; }
                return true;

            case VirtualPixelMethod.VerticalTile:
                mappedX = Math.Clamp(x, 0, width - 1);
                mappedY = ((y % height) + height) % height;
                if ((uint)x >= (uint)width) { mappedX = 0; mappedY = 0; return false; }
                return true;

            case VirtualPixelMethod.CheckerTile:
                int tileX = ((x % width) + width) % width;
                int tileY = ((y % height) + height) % height;
                int cellX = x < 0 ? (x - width + 1) / width : x / width;
                int cellY = y < 0 ? (y - height + 1) / height : y / height;
                if (((cellX + cellY) & 1) != 0)
                {
                    // Checkerboard "off" cell — use background
                    mappedX = 0; mappedY = 0;
                    return false;
                }
                mappedX = tileX;
                mappedY = tileY;
                return true;

            default:
                // Transparent, Background, White, Black, Gray, Random → constant color
                mappedX = 0;
                mappedY = 0;
                return false;
        }
    }

    /// <summary>
    /// Returns the constant RGBA value for out-of-bounds pixels when MapCoordinate returns false.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (ushort R, ushort G, ushort B, ushort A) GetConstantColor(VirtualPixelMethod method)
    {
        return method switch
        {
            VirtualPixelMethod.Transparent => (0, 0, 0, 0),
            VirtualPixelMethod.Black or VirtualPixelMethod.Background => (0, 0, 0, Quantum.MaxValue),
            VirtualPixelMethod.White => (Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue),
            VirtualPixelMethod.Gray => ((ushort)(Quantum.MaxValue / 2), (ushort)(Quantum.MaxValue / 2), (ushort)(Quantum.MaxValue / 2), Quantum.MaxValue),
            _ => (0, 0, 0, 0)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MirrorIndex(int i, int size)
    {
        if (size <= 1) return 0;
        int period = 2 * (size - 1);
        int idx = ((i % period) + period) % period;
        return idx < size ? idx : period - idx;
    }
}
