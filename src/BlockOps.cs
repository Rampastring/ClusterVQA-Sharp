using System;

namespace ClusterVQA;

/// <summary>
/// Block operations: splitting frames into block vectors and merging them back.
/// All image data is stored as flat float arrays in row-major [H, W, 3] layout.
/// </summary>
public static class BlockOps
{
    /// <summary>
    /// Crops an image to be evenly divisible by block dimensions.
    /// Returns the cropped image data, and the block counts.
    /// </summary>
    public static (float[] cropped, int blocksY, int blocksX) CropToBlocks(
        float[] img, int height, int width, Config cfg)
    {
        int blocksY = height / cfg.BlockH;
        int blocksX = width / cfg.BlockW;
        int croppedH = blocksY * cfg.BlockH;
        int croppedW = blocksX * cfg.BlockW;

        if (croppedH == height && croppedW == width)
            return (img, blocksY, blocksX);

        var cropped = new float[croppedH * croppedW * 3];
        for (int y = 0; y < croppedH; y++)
        {
            int srcOffset = (y * width + 0) * 3;
            int dstOffset = (y * croppedW + 0) * 3;
            Array.Copy(img, srcOffset, cropped, dstOffset, croppedW * 3);
        }
        return (cropped, blocksY, blocksX);
    }

    /// <summary>
    /// Splits a frame into block vectors.
    /// Frame is [H, W, 3] flat. Output is [blocksY*blocksX, vectorSize] flat.
    ///
    /// Equivalent to:
    ///   for each block (y, x):
    ///     copy the blockH x blockW x 3 region into outX[y * blocksX + x, :]
    /// </summary>
    public static void SplitFrame(
        ReadOnlySpan<float> frame, int frameWidth,
        int blockW, int blockH,
        Span<float> outX,
        int blocksY, int blocksX)
    {
        int vectorSize = blockH * blockW * 3;

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int vecIdx = by * blocksX + bx;
                int vecOffset = vecIdx * vectorSize;
                int writePos = 0;

                for (int py = 0; py < blockH; py++)
                {
                    int srcRow = by * blockH + py;
                    int srcCol = bx * blockW;
                    int srcOffset = (srcRow * frameWidth + srcCol) * 3;

                    for (int px = 0; px < blockW * 3; px++)
                    {
                        outX[vecOffset + writePos] = frame[srcOffset + px];
                        writePos++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Merges codebook entries back into a frame image using block indices.
    ///
    /// Equivalent to:
    ///   for each block (y, x):
    ///     code = indices[y, x]
    ///     copy codebook.Codes[code] reshaped to (blockH, blockW, 3) into the frame
    /// </summary>
    public static void MergeFrame(
        int[,] indices,
        Codebook codebook,
        int blockW, int blockH,
        Span<float> outDecoded,
        int frameWidth)
    {
        int blocksY = indices.GetLength(0);
        int blocksX = indices.GetLength(1);

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int code = indices[by, bx];
                int codeOffset = code * codebook.VectorSize;
                int readPos = 0;

                for (int py = 0; py < blockH; py++)
                {
                    int dstRow = by * blockH + py;
                    int dstCol = bx * blockW;
                    int dstOffset = (dstRow * frameWidth + dstCol) * 3;

                    for (int px = 0; px < blockW * 3; px++)
                    {
                        outDecoded[dstOffset + px] = codebook.Codes[codeOffset + readPos];
                        readPos++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Nearest-neighbor upscaling of a 2D bool mask by block dimensions.
    /// Input: [blocksY, blocksX] -> Output: [blocksY*blockH, blocksX*blockW]
    /// </summary>
    public static bool[,] UpscaleDeltaMask(bool[,] mask, int blockW, int blockH)
    {
        int blocksY = mask.GetLength(0);
        int blocksX = mask.GetLength(1);
        var result = new bool[blocksY * blockH, blocksX * blockW];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                bool val = mask[by, bx];
                for (int py = 0; py < blockH; py++)
                {
                    for (int px = 0; px < blockW; px++)
                    {
                        result[by * blockH + py, bx * blockW + px] = val;
                    }
                }
            }
        }

        return result;
    }
}
