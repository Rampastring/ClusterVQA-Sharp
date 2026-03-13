using System;
using System.IO;

namespace ClusterVQA;

public class AudioTrack
{
    public int Rate { get; set; } = 4800;
    public int UncompSize { get; set; } = 0;
    public byte[]? Data { get; set; }

    public virtual int NumChannels => throw new NotImplementedException();
    public virtual int BitsPerChannel => throw new NotImplementedException();

    public int BytesPerFrame => (BitsPerChannel == 16 ? 2 : 1) * NumChannels;

    /// <summary>Clip length in seconds.</summary>
    public double Length => (UncompSize / (double)BytesPerFrame) / Rate;
}

public class RawS16LETrack : AudioTrack
{
    public bool IsStereo { get; set; } = true;

    public override int NumChannels => IsStereo ? 2 : 1;
    public override int BitsPerChannel => 16;

    public static RawS16LETrack LoadFromFile(string path, int rate, bool isStereo)
    {
        var track = new RawS16LETrack
        {
            Rate = rate,
            IsStereo = isStereo
        };

        byte[] data = File.ReadAllBytes(path);
        track.Data = data;
        track.UncompSize = data.Length;

        return track;
    }
}
