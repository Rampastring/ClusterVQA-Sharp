using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClusterVQA;

/// <summary>
/// Diagnostic tool: prints offsets and names of top-level VQA chunks.
/// </summary>
public static class ChunkPrinter
{
    private static int _frame;

    private static readonly HashSet<string> HasSubchunks = new() { "VQFR", "VQFL" };

    public static void PrintFile(string path)
    {
        _frame = 0;

        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs, Encoding.ASCII);

        string form = ReadAscii(reader, 4);
        if (form != "FORM")
            throw new InvalidDataException("Expected FORM");
        uint size = ReadU32BE(reader);
        string wvqa = ReadAscii(reader, 4);
        if (wvqa != "WVQA")
            throw new InvalidDataException("Expected WVQA");

        while (PrintChunks(fs, reader, fs.Position + size))
        {
        }
    }

    private static bool PrintChunks(Stream f, BinaryReader reader, long endpoint, int level = 0)
    {
        long ofs = f.Position;
        if (ofs >= endpoint)
            return false;

        if (f.Position + 8 > f.Length)
            return false;

        string chunkId = ReadAscii(reader, 4);
        if (chunkId.Length < 4)
            return false;
        uint chunkSize = ReadU32BE(reader);

        string indent = new string(' ', level * 4);
        Console.WriteLine($"{ofs:X8} [{_frame,3}] {indent} {chunkId} ({chunkSize} bytes)");

        if (chunkId is "VPRZ" or "VPTR")
            _frame++;

        if (HasSubchunks.Contains(chunkId))
        {
            while (PrintChunks(f, reader, ofs + 8 + chunkSize, level + 1))
            {
            }
        }
        else
        {
            byte[] data = reader.ReadBytes((int)chunkSize);

            switch (chunkId)
            {
                case "LINF": PrintLinf(data); break;
                case "CINF": PrintCinf(data); break;
                case "FINF": PrintFinf(data); break;
                case "VQHD": PrintHeader(data); break;
            }

            if (chunkSize % 2 == 1)
                f.Seek(1, SeekOrigin.Current);
        }

        return true;
    }

    private static void PrintLinf(byte[] data)
    {
        int pos = 0;
        Console.WriteLine("LINF chunk contents:");

        AssertTag(data, ref pos, "LINH");
        uint linhSize = ReadU32BE(data, ref pos);
        ushort cbNum = ReadU16(data, ref pos);
        ushort val1 = ReadU16(data, ref pos);
        pos += 2; // skip 2 bytes
        Console.WriteLine($"  cbnum: {cbNum}");
        Console.WriteLine($"  val1: {val1}");

        AssertTag(data, ref pos, "LIND");
        uint lindSize = ReadU32BE(data, ref pos);
        for (int i = 0; i < cbNum; i++)
        {
            ushort a = ReadU16(data, ref pos);
            ushort b = ReadU16(data, ref pos);
            Console.WriteLine($"  {i}: {a}, {b}");
        }
    }

    private static void PrintCinf(byte[] data)
    {
        int pos = 0;
        Console.WriteLine("CINF chunk contents:");

        AssertTag(data, ref pos, "CINH");
        uint cinhSize = ReadU32BE(data, ref pos);
        ushort cbNum = ReadU16(data, ref pos);
        ushort spread = ReadU16(data, ref pos);
        pos += 4;
        Console.WriteLine($"  cbnum: {cbNum}");
        Console.WriteLine($"  spread: {spread}");

        AssertTag(data, ref pos, "CIND");
        uint cindSize = ReadU32BE(data, ref pos);
        for (int i = 0; i < cbNum; i++)
        {
            ushort a = ReadU16(data, ref pos);
            uint b = ReadU32LE(data, ref pos);
            Console.WriteLine($"  {i}: {a}, {b}");
        }
    }

    private static void PrintFinf(byte[] data)
    {
        int pos = 0;
        Console.WriteLine("FINF chunk contents:");
        int frame = 0;
        while (pos + 4 <= data.Length)
        {
            uint addr = 2 * ReadU32LE(data, ref pos);
            Console.WriteLine($"  {frame}: 0x{addr:X8}");
            frame++;
        }
    }

    private static void PrintHeader(byte[] data)
    {
        int pos = 0;
        Console.WriteLine("Header:");
        Console.WriteLine($"  Version: {ReadU16(data, ref pos)}");
        Console.WriteLine($"  Flags: {ReadU16(data, ref pos)}");
        Console.WriteLine($"  Num frames:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Image width:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Image height:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Block width:\t{data[pos++]}");
        Console.WriteLine($"  Block height:\t{data[pos++]}");
        Console.WriteLine($"  FPS:\t{data[pos++]}");
        Console.WriteLine($"  Group size:\t{data[pos++]}");
        Console.WriteLine($"  Colors:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Max blocks:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  X pos:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Y pos:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Max framesize:\t{ReadU16(data, ref pos)}");

        Console.WriteLine($"  Sound sample rate:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Sound channels:\t{data[pos++]}");
        Console.WriteLine($"  Sound bits per sample:\t{data[pos++]}");

        Console.WriteLine($"  Alt sample rate:\t{ReadU16(data, ref pos)}");
        Console.WriteLine($"  Alt channels:\t{data[pos++]}");
        Console.WriteLine($"  Alt bits per sample:\t{data[pos++]}");
        Console.WriteLine($"  Color mode:\t{data[pos++]}");
        Console.WriteLine($"  Unknown6:\t{data[pos++]}");
        Console.WriteLine($"  Largest CBFZ:\t{ReadU32LE(data, ref pos)}");
        Console.WriteLine($"  Unknown7:\t{ReadU32LE(data, ref pos)}");
    }

    // ---- Binary reading helpers ----

    private static string ReadAscii(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        return Encoding.ASCII.GetString(bytes);
    }

    private static uint ReadU32BE(BinaryReader reader)
    {
        byte[] b = reader.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static void AssertTag(byte[] data, ref int pos, string expected)
    {
        string tag = Encoding.ASCII.GetString(data, pos, 4);
        pos += 4;
        if (tag != expected)
            throw new InvalidDataException($"Expected {expected}, got {tag}");
    }

    private static uint ReadU32BE(byte[] data, ref int pos)
    {
        uint val = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;
        return val;
    }

    private static ushort ReadU16(byte[] data, ref int pos)
    {
        ushort val = (ushort)(data[pos] | (data[pos + 1] << 8));
        pos += 2;
        return val;
    }

    private static uint ReadU32LE(byte[] data, ref int pos)
    {
        uint val = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
        pos += 4;
        return val;
    }
}
