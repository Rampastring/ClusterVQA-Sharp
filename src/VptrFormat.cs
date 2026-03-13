using System;
using System.Collections.Generic;

namespace ClusterVQA;

/// <summary>
/// VPTR block map run length encoding (RLE) routines.
/// </summary>
public static class VptrFormat
{
    /// <summary>
    /// Decompresses a VPTR-encoded buffer into a flat ushort array.
    /// </summary>
    public static void Decompress(byte[] buf, ushort[] output)
    {
        int sp = 0;
        int cur = 0;
        int N = buf.Length;

        byte Read() => buf[sp++];

        while (sp < N)
        {
            int val = Read();
            val |= Read() << 8;

            int action = (val & 0xE000) >> 13;
            switch (action)
            {
                case 0:
                {
                    int count = val & 0x1FFF;
                    cur += count;
                    break;
                }
                case 1:
                {
                    int block = val & 0xFF;
                    int count = (((val >> 8) & 0x1F) + 1) << 1;
                    for (int i = 0; i < count; i++)
                        output[cur++] = (ushort)block;
                    break;
                }
                case 2:
                {
                    int block = val & 0xFF;
                    int count = (((val >> 8) & 0x1F) + 1) << 1;
                    output[cur++] = (ushort)block;
                    for (int i = 0; i < count; i++)
                        output[cur++] = Read();
                    break;
                }
                case 3:
                case 4:
                {
                    output[cur++] = (ushort)(val & 0x1FFF);
                    break;
                }
                case 5:
                case 6:
                {
                    int count = Read();
                    ushort value = (ushort)(val & 0x1FFF);
                    for (int i = 0; i < count; i++)
                        output[cur++] = value;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Compresses a 2D block index array with a validity mask into VPTR format.
    /// </summary>
    /// <param name="buf">2D block indices [yblocks, xblocks].</param>
    /// <param name="validMask">2D bool mask [yblocks, xblocks].</param>
    /// <param name="yblocks">Number of vertical blocks.</param>
    /// <param name="xblocks">Number of horizontal blocks.</param>
    public static byte[] Compress(int[,] buf, bool[,] validMask, int yblocks, int xblocks)
    {
        var output = new List<byte>();

        void WriteUint(int x)
        {
            output.Add((byte)(x & 0xFF));
            output.Add((byte)(x >> 8));
        }

        for (int y = 0; y < yblocks; y++)
        {
            int x = 0;
            while (x < xblocks)
            {
                int skipLen = 0;
                while (x < xblocks && !validMask[y, x])
                {
                    skipLen++;
                    x++;
                }

                if (skipLen > 0)
                    WriteUint(skipLen); // action 0: skip

                int runX = x;
                while (runX < xblocks && validMask[y, runX])
                    runX++;

                int runLen = runX - x;
                if (runLen > 0)
                {
                    for (int i = 0; i < runLen; i++)
                    {
                        // action 6: single block write
                        WriteUint(0x6000 | (buf[y, x] & 0x1FFF));
                        x++;
                    }
                }
            }
        }

        return output.ToArray();
    }
}
