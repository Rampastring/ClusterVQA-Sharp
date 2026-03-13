using System;
using System.Collections.Generic;

namespace ClusterVQA;

/// <summary>
/// Routines for Westwood LCW ("format80") compression.
/// See: https://moddingwiki.shikadi.net/wiki/Westwood_LCW
/// </summary>
public static class Lcw
{
    /// <summary>
    /// Decompresses an LCW compressed buffer.
    /// </summary>
    /// <param name="buf">Compressed input bytes.</param>
    /// <param name="expectedOutput">Optional expected output for verification (debug).</param>
    public static byte[] Decompress(byte[] buf, byte[]? expectedOutput = null)
    {
        bool newFormat = buf[0] == 0;
        int inputSize = buf.Length;

        int start = newFormat ? 1 : 0;
        int inpos = start;
        var output = new List<byte>(inputSize * 2);
        int lastDp = 0;

        byte Read()
        {
            byte x = buf[inpos];
            inpos++;
            return x;
        }

        while (inpos - start < inputSize)
        {
            byte a = Read();

            if (a == 0x80)
                break;

            if ((a & 0x80) == 0)
            {
                // Command 2
                int count = (a >> 4) + 3;
                int relpos = ((a & 0x0F) << 8) | Read();
                int src = output.Count - relpos;
                for (int i = src; i < src + count; i++)
                    output.Add(output[i]);
            }
            else
            {
                if (a == 0xFE)
                {
                    // Command 4
                    int count = Read();
                    count |= Read() << 8;
                    byte value = Read();
                    for (int i = 0; i < count; i++)
                        output.Add(value);
                }
                else if (a == 0xFF)
                {
                    // Command 5
                    int count = Read();
                    count |= Read() << 8;
                    int src = Read();
                    src |= Read() << 8;
                    if (newFormat)
                        src = output.Count - src;
                    for (int i = src; i < src + count; i++)
                        output.Add(output[i]);
                }
                else if ((a >> 6) == 2)
                {
                    // Command 1
                    int count = a & 0x3F;
                    for (int i = 0; i < count; i++)
                        output.Add(Read());
                }
                else if ((a >> 6) == 3)
                {
                    // Command 3
                    int count = (a & 0x3F) + 3;
                    int src = Read();
                    src |= Read() << 8;
                    if (newFormat)
                        src = output.Count - src;
                    for (int i = src; i < src + count; i++)
                        output.Add(output[i]);
                }
                else
                {
                    throw new InvalidOperationException($"format80 command {a} not handled");
                }
            }

            if (expectedOutput != null)
            {
                for (int i = lastDp; i < output.Count; i++)
                {
                    if (output[i] != expectedOutput[i])
                        throw new InvalidOperationException(
                            $"Verification failed at offset {i}: got {output[i]}, expected {expectedOutput[i]}");
                }
            }

            lastDp = output.Count;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compresses data using Westwood LCW ("format80").
    /// </summary>
    /// <param name="data">Uncompressed input.</param>
    /// <param name="newFormat">If true, uses relative offsets (new format with a 0x00 leader byte).</param>
    public static byte[] Compress(byte[] data, bool newFormat = false)
    {
        int N = data.Length;
        var output = new List<byte>(N);
        var literalRun = new List<byte>();

        if (newFormat)
            output.Add(0);

        void FlushLiterals()
        {
            if (literalRun.Count == 0)
                return;

            // Emit command 1
            int count = literalRun.Count;
            output.Add((byte)(0x80 | count));
            output.AddRange(literalRun);
            literalRun.Clear();
        }

        void EmitLiteral(byte x)
        {
            literalRun.Add(x);
            if (literalRun.Count == 0x3F)
                FlushLiterals();
        }

        void EmitRef(int sp, int matchStart, int count)
        {
            FlushLiterals();
            int relofs = sp - matchStart;
            int absofs = newFormat ? (sp - matchStart) : matchStart;

            if (count <= 10 && relofs <= 0xFFF)
            {
                // Emit command 2
                byte a = (byte)(((count - 3) << 4) | ((relofs >> 8) & 0x0F));
                byte b = (byte)(relofs & 0xFF);
                output.Add(a);
                output.Add(b);
            }
            else if (count <= 64 && absofs <= 0xFFFF)
            {
                // Emit command 3
                byte a = (byte)(0xC0 | ((count - 3) & 0x3F));
                output.Add(a);
                output.Add((byte)(absofs & 0xFF));
                output.Add((byte)(absofs >> 8));
            }
            else
            {
                throw new InvalidOperationException($"run ({relofs}, {count}) couldn't be encoded");
            }
        }

        const int MaxRefDistance = 4095;
        const int SearchWindow = 8;
        const int MaxRunLength = 64;
        int sp = 0;

        // Hash table: maps 4-byte sequences to their last seen offset
        var seen = new Dictionary<int, int>();

        int HashKey(int pos)
        {
            // Simple hash of 4 bytes
            return data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
        }

        while (sp < N)
        {
            // Try the hash table first for longer-distance matches
            if (sp < N - 4)
            {
                int key = HashKey(sp);
                if (seen.TryGetValue(key, out int ofs))
                {
                    seen[key] = sp;
                    if (sp - ofs <= MaxRefDistance || !newFormat)
                    {
                        int cur = sp + 4;
                        int j = ofs + 4;
                        while (cur < N && (j - ofs) < MaxRunLength && data[cur] == data[j])
                        {
                            cur++;
                            j++;
                        }
                        int count = j - ofs;

                        EmitRef(sp, ofs, count);
                        sp += count;
                        continue;
                    }
                }
                else
                {
                    seen[key] = sp;
                }
            }

            // Fallback: brute-force search within a small window
            bool found = false;
            for (int i = sp - 1; i >= Math.Max(0, sp - SearchWindow); i--)
            {
                int cur = sp;
                int j = i;
                while (cur < N && (j - i) < MaxRunLength && data[cur] == data[j])
                {
                    cur++;
                    j++;
                }
                int count = j - i;
                if (count >= 3)
                {
                    EmitRef(sp, i, count);
                    sp += count;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                EmitLiteral(data[sp]);
                sp++;
            }
        }

        FlushLiterals();
        output.Add(0x80);

        return output.ToArray();
    }
}
