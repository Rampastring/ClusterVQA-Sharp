using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using ClusterVQA;

var defaultCfg = new Config();

var frameDirArg = new Argument<string>("framedir", "PNG image sequence location");
var outputArg = new Argument<string>("output", "Output file name");

var fpsOption = new Option<int>("--fps", () => 15, "Video speed in frames per second");
var blocksOption = new Option<int>("--blocks", () => defaultCfg.NumBlocks,
    "Codes per codebook (500-8000). Higher = better quality, slower encoding.");
var blockSizeOption = new Option<string>("--blocksize", () => "4x4",
    "Block size: 4x4 or 4x2. 4x2 yields higher quality at the expense of file size.");
blockSizeOption.FromAmong("4x4", "4x2");

var audioOption = new Option<string?>("--audio", "Soundtrack in raw pcm_s16le format");
var audioRateOption = new Option<int>("--audiorate", () => 44100, "Audio sampling rate in Hz");
audioRateOption.AddAlias("-ar");

var minKfOption = new Option<int>("--min-keyframe-distance", () => defaultCfg.MinKeyframeDistance,
    "Never encode two codebooks closer than this many frames.");
var maxKfOption = new Option<int>("--max-keyframe-distance", () => defaultCfg.MaxKeyframeDistance,
    "Always encode a codebook if this many frames have passed.");
var blockErrOption = new Option<double>("--block-replacement-error", () => defaultCfg.BlockReplacementError,
    "Accumulated error threshold for re-encoding a block (0-100). 0 = re-encode every frame.");
var sceneCutOption = new Option<double>("--scene-cut-error-limit", () => defaultCfg.SceneCutErrorLimit,
    "Error ratio to trigger a new codebook (1-10). Lower = more sensitive.");
var ditherOption = new Option<double>("--dither-strength", () => defaultCfg.DitherStrength,
    "Ordered dithering strength (0-10).");
var maxVecOption = new Option<int>("--max-vectors-to-fit", () => defaultCfg.MaxVectorsToFit,
    "Higher = better quality, slower. Reasonable: 10000-300000.");
var quantBeforeOption = new Option<bool>("--quantize-before-block-search", () => defaultCfg.QuantizeBeforeBlockSearch,
    "Quantize colors before codebook fitting.");
var verboseOption = new Option<bool>("--verbose", "Print detailed logging messages.");

var rootCommand = new RootCommand("ClusterVQA - Encode VQA videos for C&C: Tiberian Sun")
{
    frameDirArg, outputArg,
    fpsOption, blocksOption, blockSizeOption,
    audioOption, audioRateOption,
    minKfOption, maxKfOption,
    blockErrOption, sceneCutOption,
    ditherOption, maxVecOption,
    quantBeforeOption, verboseOption
};

rootCommand.SetHandler((context) =>
{
    var result = context.ParseResult;

    string frameDir = result.GetValueForArgument(frameDirArg);
    string output = result.GetValueForArgument(outputArg);
    int fps = result.GetValueForOption(fpsOption);
    int blocks = result.GetValueForOption(blocksOption);
    string blockSize = result.GetValueForOption(blockSizeOption) ?? "4x4";
    string? audioPath = result.GetValueForOption(audioOption);
    int audioRate = result.GetValueForOption(audioRateOption);
    int minKf = result.GetValueForOption(minKfOption);
    int maxKf = result.GetValueForOption(maxKfOption);
    double blockErr = result.GetValueForOption(blockErrOption);
    double sceneCut = result.GetValueForOption(sceneCutOption);
    double dither = result.GetValueForOption(ditherOption);
    int maxVec = result.GetValueForOption(maxVecOption);
    bool quantBefore = result.GetValueForOption(quantBeforeOption);
    bool verbose = result.GetValueForOption(verboseOption);

    RuntimeConfig.Verbose = verbose;

    var cfg = new Config
    {
        NumBlocks = blocks,
        MinKeyframeDistance = minKf,
        MaxKeyframeDistance = maxKf,
        SceneCutErrorLimit = sceneCut,
        BlockReplacementError = blockErr,
        MaxVectorsToFit = maxVec,
        DitherStrength = dither,
        QuantizeBeforeBlockSearch = quantBefore,
    };

    switch (blockSize)
    {
        case "4x4": cfg.BlockW = 4; cfg.BlockH = 4; break;
        case "4x2": cfg.BlockW = 4; cfg.BlockH = 2; break;
    }

    if (minKf < 15)
        Console.WriteLine($"Warning: The set minimum keyframe distance {minKf} is very short and may produce a video that doesn't play in game");

    AudioTrack? track = null;
    if (audioPath != null)
    {
        track = RawS16LETrack.LoadFromFile(audioPath, audioRate, isStereo: true);
        Console.WriteLine($"Loaded audio {audioPath}: {track.Rate} Hz, Channels: {track.NumChannels}, {track.BitsPerChannel} bps, {track.Length:F2} s");
    }

    string[] framePaths = Directory.GetFiles(frameDir, "*.png")
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    Console.WriteLine($"Encoding {framePaths.Length} frames of {frameDir} into {output}");

    // Create the file if missing, then open for read & write
    using var fs = new FileStream(output, FileMode.OpenOrCreate, FileAccess.ReadWrite);
    var encoder = new HiColorVqaEncoder(fs, track);
    Quantizer.EncodeVideo(framePaths, fps, cfg, encoder);
});

return rootCommand.Invoke(args);
