namespace ClusterVQA;

public class Config
{
    public int BlockW { get; set; } = 4;
    public int BlockH { get; set; } = 4;
    public int NumBlocks { get; set; } = 3000;
    public double DitherStrength { get; set; } = 4.0;
    public bool QuantizeBeforeBlockSearch { get; set; } = true;
    public int MaxKeyframeDistance { get; set; } = 30;
    public int MinKeyframeDistance { get; set; } = 15;
    public double SceneCutErrorLimit { get; set; } = 3.0;
    public double BlockReplacementError { get; set; } = 2.0;
    public int MaxVectorsToFit { get; set; } = 200000;
    public bool StereoAudio { get; set; } = false;

    public int VectorSize => BlockH * BlockW * 3;
}

public class VideoHeader
{
    public int Version { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }
    public int NumFrames { get; set; }
    public double Fps { get; set; }
    public Config Cfg { get; set; } = new();

    public VideoHeader(int width, int height, int numFrames, double fps, Config cfg)
    {
        Width = width;
        Height = height;
        NumFrames = numFrames;
        Fps = fps;
        Cfg = cfg;
    }
}

/// <summary>
/// A decoded frame stored as a flat float array in [height, width, 3] layout (YUV or RGB).
/// </summary>
public class RawFrame
{
    /// <summary>Pixel data: float[Height * Width * 3] in row-major [H, W, 3] order.</summary>
    public float[] Yuv { get; }
    public int Height { get; }
    public int Width { get; }
    public int BlocksX { get; }
    public int BlocksY { get; }

    public RawFrame(float[] yuv, int width, int height, int blocksX, int blocksY)
    {
        Yuv = yuv;
        Width = width;
        Height = height;
        BlocksX = blocksX;
        BlocksY = blocksY;
    }

    public int BlocksPerFrame => BlocksY * BlocksX;
}

/// <summary>
/// Codebook: float[NumCodes, VectorSize] stored as a flat array.
/// </summary>
public class Codebook
{
    public float[] Codes { get; }
    public int NumCodes { get; }
    public int VectorSize { get; }

    public Codebook(float[] codes, int numCodes, int vectorSize)
    {
        Codes = codes;
        NumCodes = numCodes;
        VectorSize = vectorSize;
    }
}

public class EncodedFrame
{
    public int[,] Indices { get; }
    public bool[,] DeltaMask { get; }

    public EncodedFrame(int[,] indices, bool[,] deltaMask)
    {
        Indices = indices;
        DeltaMask = deltaMask;
    }
}
