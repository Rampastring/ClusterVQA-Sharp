using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClusterVQA;

/// <summary>
/// Base encoder interface for VQA video output.
/// </summary>
public abstract class Encoder
{
    public abstract void WriteHeader(VideoHeader header);
    public abstract void WriteCodebook(Codebook codebook);
    public abstract void WriteFrame(int[,] indices, bool[,] deltaMask);
    public virtual void FinalizeOutput() { }
}

/// <summary>
/// Encodes a codebook's float vectors into packed RGB555 bytes for writing into CBFZ chunks.
/// Each pixel is packed as "0rrrrrgg gggbbbbb" (16-bit, little-endian in output).
/// </summary>
internal static class CodebookEncoder
{
    public static byte[] Encode(Codebook codebook, Config cfg)
    {
        int pixelsPerCode = cfg.BlockH * cfg.BlockW;
        int numCodes = codebook.NumCodes;

        // Allocate output: 2 bytes per pixel
        var packed = new byte[numCodes * pixelsPerCode * 2];

        // Temporary buffer for perceptual -> RGB conversion (3 floats per pixel per code)
        var rgbFloats = new float[codebook.Codes.Length];
        Conversions.Perceptual2Rgb(codebook.Codes, rgbFloats);

        Parallel.For(0, numCodes, code =>
        {
            int codeBaseOffset = code * codebook.VectorSize;
            int outIdx = code * pixelsPerCode * 2;
            for (int p = 0; p < pixelsPerCode; p++)
            {
                int pixOffset = codeBaseOffset + p * 3;
                int r5 = Conversions.RoundToRgb555(rgbFloats[pixOffset]);
                int g5 = Conversions.RoundToRgb555(rgbFloats[pixOffset + 1]);
                int b5 = Conversions.RoundToRgb555(rgbFloats[pixOffset + 2]);

                // Pack: 0rrrrrgggggbbbbb
                ushort rgb555 = (ushort)((r5 << 10) | (g5 << 5) | b5);
                packed[outIdx++] = (byte)(rgb555 & 0xFF);
                packed[outIdx++] = (byte)(rgb555 >> 8);
            }
        });

        return packed;
    }
}

/// <summary>
/// Writes a Hi-Color VQA file for Tiberian Sun.
/// </summary>
public class HiColorVqaEncoder : Encoder
{
    private readonly Stream _file;
    private readonly AudioTrack? _track;
    private readonly BinaryWriter _writer;

    private VideoHeader? _header;
    private Codebook? _codebook;
    private Codebook? _newCodebook;
    private int _frameNum;
    private readonly List<long> _frameDataOffsets = new();
    private double _audioSamplePos;
    private readonly List<(int frameNum, long offset, long size)> _cbfzChunkInfos = new();

    public HiColorVqaEncoder(Stream file, AudioTrack? track)
    {
        _file = file;
        _track = track;
        _writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
    }

    // ---- Primitive writers ----

    private void WriteU8(byte x) => _writer.Write(x);
    private void WriteU16(ushort x) => _writer.Write(x); // little-endian by default in BinaryWriter
    private void WriteU32(uint x) => _writer.Write(x);

    private void WriteU32BE(uint x)
    {
        _writer.Write((byte)((x >> 24) & 0xFF));
        _writer.Write((byte)((x >> 16) & 0xFF));
        _writer.Write((byte)((x >> 8) & 0xFF));
        _writer.Write((byte)(x & 0xFF));
    }

    private void WriteAscii(string s)
    {
        foreach (char c in s)
            _writer.Write((byte)c);
    }

    // ---- Chunk helper ----
    // The Python code uses a context manager to write a chunk header, yield, then patch the size.
    // In C# we use a disposable helper that patches on Dispose().

    private ChunkScope BeginChunk(string name)
    {
        return new ChunkScope(_file, _writer, name);
    }

    private sealed class ChunkScope : IDisposable
    {
        private readonly Stream _stream;
        private readonly BinaryWriter _writer;
        public long DataOffset { get; }

        public ChunkScope(Stream stream, BinaryWriter writer, string name)
        {
            _stream = stream;
            _writer = writer;

            // Write chunk name (4 ASCII bytes)
            foreach (char c in name)
                writer.Write((byte)c);

            // Write placeholder size (big-endian u32)
            writer.Write((byte)'x');
            writer.Write((byte)'x');
            writer.Write((byte)'x');
            writer.Write((byte)'x');

            DataOffset = stream.Position;
        }

        public void Dispose()
        {
            long current = _stream.Position;
            long size = current - DataOffset;

            // Patch chunk size (big-endian u32 at DataOffset - 4)
            _stream.Seek(DataOffset - 4, SeekOrigin.Begin);
            _writer.Write((byte)((size >> 24) & 0xFF));
            _writer.Write((byte)((size >> 16) & 0xFF));
            _writer.Write((byte)((size >> 8) & 0xFF));
            _writer.Write((byte)(size & 0xFF));

            _stream.Seek(current, SeekOrigin.Begin);

            // Next chunk needs to be 16-bit aligned
            if (_stream.Position % 2 == 1)
                _writer.Write((byte)0);
        }
    }

    // ---- Encoder interface ----

    public override void WriteHeader(VideoHeader header)
    {
        // Header gets written in Finalize() after all the video stream data
        // because we need to know the number of keyframes at that point.
        _header = header;
        _file.SetLength(0); // Truncate
    }

    public override void WriteCodebook(Codebook codebook)
    {
        _newCodebook = codebook;
    }

    private void WriteCbfz(Codebook codebook)
    {
        using var chunk = BeginChunk("CBFZ");
        byte[] data = CodebookEncoder.Encode(codebook, _header!.Cfg);
        byte[] cbfzData = Lcw.Compress(data, newFormat: true);
        _file.Write(cbfzData);
        long size = _file.Position - chunk.DataOffset;
        _cbfzChunkInfos.Add((_frameNum, chunk.DataOffset, size));
    }

    private void WriteSnd0()
    {
        var track = _track!;
        int start = (int)Math.Round(_audioSamplePos);
        _audioSamplePos += track.Rate / _header!.Fps;
        int end = (int)Math.Round(_audioSamplePos);
        int frameSize = track.BytesPerFrame;
        int byteStart = Math.Min(frameSize * start, track.Data!.Length);
        int byteEnd = Math.Min(frameSize * end, track.Data!.Length);

        using var chunk = BeginChunk("SND0");
        _file.Write(track.Data!, byteStart, byteEnd - byteStart);
    }

    public override void WriteFrame(int[,] indices, bool[,] deltaMask)
    {
        bool writeNewCodebook = _newCodebook != null;
        long finfOffset = _file.Position;

        if (writeNewCodebook)
        {
            _codebook = _newCodebook;
            _newCodebook = null;
        }

        _frameDataOffsets.Add(finfOffset);

        if (_frameNum > 0 && writeNewCodebook)
        {
            using var vqfl = BeginChunk("VQFL");
            WriteCbfz(_codebook!);
        }

        // Sound chunks must be written after VQFL because otherwise codebook updates won't work ingame.
        if (_track != null)
            WriteSnd0();

        using (var vqfr = BeginChunk("VQFR"))
        {
            if (_frameNum == 0 && writeNewCodebook)
                WriteCbfz(_codebook!);

            using (var vprz = BeginChunk("VPRZ"))
            {
                byte[] rawVptr = VptrFormat.Compress(indices, deltaMask,
                    indices.GetLength(0), indices.GetLength(1));
                byte[] vprzData = Lcw.Compress(rawVptr, newFormat: true);
                _file.Write(vprzData);
            }
        }

        _frameNum++;
    }

    public override void FinalizeOutput()
    {
        // Read back all stream data written so far, then prepend the headers.
        long fileSize = _file.Position;
        _file.Seek(0, SeekOrigin.Begin);
        byte[] streamData = new byte[fileSize];
        _file.ReadExactly(streamData, 0, (int)fileSize);
        _file.Seek(0, SeekOrigin.Begin);

        var hdr = _header!;

        // "FORMxxxxWVQA"
        WriteAscii("FORM");
        WriteAscii("xxxx"); // placeholder for total size
        WriteAscii("WVQA");

        // VQHD chunk (42 bytes of header data)
        WriteAscii("VQHD");
        WriteU32BE(42);

        if (Math.Floor(hdr.Fps) != hdr.Fps)
            Console.WriteLine($"Non-integral FPS {hdr.Fps} will be truncated");

        WriteU16(3); // version
        WriteU16((ushort)(_track == null ? 0x1C : 0x1D));
        WriteU16((ushort)hdr.NumFrames);
        WriteU16((ushort)hdr.Width);
        WriteU16((ushort)hdr.Height);
        WriteU8((byte)hdr.Cfg.BlockW);
        WriteU8((byte)hdr.Cfg.BlockH);
        WriteU8((byte)(int)hdr.Fps);
        WriteU8(0); // Codebook parts (0 for Hi-Color VQAs)
        WriteU16(0); // Colors (always 0 in Hi-Color)
        WriteU16((ushort)hdr.Cfg.NumBlocks); // MaxBlocks
        WriteU32(0); // Unknown1
        WriteU16(32765); // Max frame size

        if (_track == null)
        {
            WriteU16(0); // Freq
            WriteU8(0);  // Channels
            WriteU8(0);  // Sound resolution
        }
        else
        {
            WriteU16((ushort)_track.Rate);
            WriteU8((byte)_track.NumChannels);
            WriteU8((byte)_track.BitsPerChannel);
        }

        WriteU32(0); // Unknown3
        WriteU16(4); // Unknown4, always 4

        // Compute largest CBFZ chunk size
        long maxCbfzSize = 0;
        foreach (var (_, _, size) in _cbfzChunkInfos)
            maxCbfzSize = Math.Max(maxCbfzSize, size);
        WriteU32((uint)maxCbfzSize);
        WriteU32(0); // Unknown5

        int cbNum = _cbfzChunkInfos.Count;

        // LINF chunk: loop information header (LINH) and data (LIND)
        using (BeginChunk("LINF"))
        {
            using (BeginChunk("LINH"))
            {
                WriteU16((ushort)cbNum);
                WriteU16(2);
                WriteU16(0);
            }

            using (BeginChunk("LIND"))
            {
                foreach (var (frameNum, _, _) in _cbfzChunkInfos)
                {
                    WriteU16((ushort)frameNum);
                    WriteU16((ushort)(hdr.NumFrames - 1));
                }
            }
        }

        // 'spread' = minimum number of frames between codebook updates
        int spread = hdr.NumFrames;
        if (cbNum > 1)
        {
            for (int i = 0; i < _cbfzChunkInfos.Count - 1; i++)
            {
                spread = Math.Min(spread,
                    _cbfzChunkInfos[i + 1].frameNum - _cbfzChunkInfos[i].frameNum);
            }
        }

        // CINF chunk: codebook information header (CINH) and data (CIND)
        using (BeginChunk("CINF"))
        {
            using (BeginChunk("CINH"))
            {
                WriteU16((ushort)cbNum);
                WriteU16((ushort)spread);
                WriteU32(0);
            }

            using (BeginChunk("CIND"))
            {
                foreach (var (frameNum, _, size) in _cbfzChunkInfos)
                {
                    WriteU16((ushort)frameNum);
                    WriteU32((uint)size);
                }
            }
        }

        // FINF chunk: frame offset table
        long finfDataStart;
        using (var finfChunk = BeginChunk("FINF"))
        {
            finfDataStart = finfChunk.DataOffset;
            foreach (var _ in _frameDataOffsets)
                WriteU32(0); // placeholders
        }

        long totalHeadersSize = _file.Position;

        // Patch FINF with adjusted offsets (frames shifted forward by header size)
        _file.Seek(finfDataStart, SeekOrigin.Begin);
        foreach (long ofs in _frameDataOffsets)
        {
            long finfAddr = ofs + totalHeadersSize;
            WriteU32((uint)(finfAddr / 2)); // FINF offsets are always 16-bit aligned, so /2
        }

        _file.Seek(totalHeadersSize, SeekOrigin.Begin);

        // Rewrite the actual video data after the header
        _file.Write(streamData);

        fileSize = _file.Position;

        // Patch total file size after the FORM chunk id
        _file.Seek(4, SeekOrigin.Begin);
        WriteU32BE((uint)(fileSize - 8));
        _file.Seek(fileSize, SeekOrigin.Begin);

        _writer.Flush();
    }
}
