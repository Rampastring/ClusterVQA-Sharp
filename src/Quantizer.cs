using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace ClusterVQA;

/// <summary>
/// A clustering vector quantizer. Reads frames, splits them into block vectors,
/// clusters with K-means, assigns indices, and passes frames + codebook to an Encoder.
/// </summary>
public static class Quantizer
{
    private static void LogVerbose(string msg)
    {
        if (RuntimeConfig.Verbose)
            Console.WriteLine(msg);
    }

    /// <summary>
    /// Load an image file as a float RGB array [H*W*3] with values 0..1.
    /// Uses System.Drawing on Windows; for cross-platform use, swap in ImageSharp/SkiaSharp.
    /// </summary>
    public static (float[] rgb, int width, int height) LoadFloatRgb(string path)
    {
        float[] rgb;
        int w;
        int h;

        using (Image<Rgba32> image = Image.Load<Rgba32>(path))
        {
            rgb = new float[image.Width * image.Height * 3];
            w = image.Width;
            h = image.Height;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);

                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];

                        rgb[(y * row.Length + x) * 3] = pixel.R / 255.0f;
                        rgb[(y * row.Length + x) * 3 + 1] = pixel.G / 255.0f;
                        rgb[(y * row.Length + x) * 3 + 2] = pixel.B / 255.0f;
                    }
                }
            });
        }

        return (rgb, w, h);
    }

    /// <summary>
    /// Loads and prepares a frame: applies dithering, optional pre-quantization, color space conversion, and block cropping.
    /// </summary>
    public static RawFrame LoadAndPrepareFrame(string path, Config cfg, bool convertToYuv = true)
    {
        var (imgRgb, width, height) = LoadFloatRgb(path);

        // Apply ordered dithering
        float[] dither = Conversions.Make4X4DitherTable(height, width);

        if (cfg.DitherStrength > 0.0)
        {
            float ditherScale = (float)(cfg.DitherStrength / 256.0);
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int pixIdx = (y * width + x) * 3;
                    float d = dither[y * width + x] * ditherScale;
                    imgRgb[pixIdx] = Math.Clamp(imgRgb[pixIdx] + d, 0f, 1f);
                    imgRgb[pixIdx + 1] = Math.Clamp(imgRgb[pixIdx + 1] + d, 0f, 1f);
                    imgRgb[pixIdx + 2] = Math.Clamp(imgRgb[pixIdx + 2] + d, 0f, 1f);
                }
            });
        }

        if (cfg.QuantizeBeforeBlockSearch)
        {
            // Quantize to RGB555 and expand back to float
            int totalChannels = width * height * 3;
            var quant = new int[totalChannels];
            Conversions.RoundRgbFloatToRgb555(imgRgb, quant);
            var expanded = new int[totalChannels];
            Conversions.ExpandRgb555ToRgb888(quant, expanded);
            for (int i = 0; i < totalChannels; i++)
                imgRgb[i] = expanded[i] / 255f;
        }

        float[] imgAdjusted;
        if (convertToYuv)
        {
            imgAdjusted = new float[imgRgb.Length];
            Conversions.Rgb2Perceptual(imgRgb, imgAdjusted);
        }
        else
        {
            imgAdjusted = imgRgb;
        }

        var (cropped, blocksY, blocksX) = BlockOps.CropToBlocks(imgAdjusted, height, width, cfg);
        int croppedW = blocksX * cfg.BlockW;
        int croppedH = blocksY * cfg.BlockH;
        return new RawFrame(cropped, croppedW, croppedH, blocksX, blocksY);
    }

    /// <summary>
    /// Fits K-means clusters to the given vector data.
    /// This is a self-contained Mini-Batch K-Means implementation replacing scikit-learn.
    /// </summary>
    public static float[] FitClusters(
        float[] X, int numRows, int vectorSize,
        int numClusters, int maxVectorsToFit,
        bool deduplicate = true, int seed = 0)
    {
        float[] data = X;
        int dataRows = numRows;
        int[]? counts = null;

        if (deduplicate)
        {
            (data, counts, dataRows) = DeduplicateVectors(data, numRows, vectorSize);
            LogVerbose($"Deduped {numRows} vectors into {dataRows}");
        }

        // Subsample to most common vectors if needed
        if (dataRows > maxVectorsToFit)
        {
            var sortedIndices = Enumerable.Range(0, dataRows)
                .OrderBy(i => counts![i])
                .ToArray();
            int keepCount = maxVectorsToFit;
            var keepIndices = sortedIndices.Skip(dataRows - keepCount).ToArray();

            var subData = new float[keepCount * vectorSize];
            var subCounts = new int[keepCount];
            for (int i = 0; i < keepCount; i++)
            {
                Array.Copy(data, keepIndices[i] * vectorSize, subData, i * vectorSize, vectorSize);
                subCounts[i] = counts![keepIndices[i]];
            }
            Console.WriteLine($"Subsampled clustering input to {(keepCount / (double)dataRows) * 100:F3} % most common vectors");
            data = subData;
            counts = subCounts;
            dataRows = keepCount;
        }

        int k = Math.Min(numClusters - 1, dataRows);
        float[] centers = MiniBatchKMeans(data, dataRows, vectorSize, k, counts, seed);

        // Add a black block (all zeros = black in both RGB and YUV)
        var result = new float[(k + 1) * vectorSize];
        Array.Copy(centers, 0, result, 0, k * vectorSize);
        // Last row is already zero
        return result;
    }

    /// <summary>
    /// Sorts most-used clusters to the front and drops unused ones.
    /// </summary>
    public static float[] SortAndPruneClusters(
        float[] X, int xRows,
        float[] clusters, int clusterCount,
        int vectorSize)
    {
        // Assign each vector to its nearest cluster
        var labels = new int[xRows];
        NearestNeighborAssign(X, xRows, clusters, clusterCount, vectorSize, labels);

        // Count usage per cluster
        var usageCounts = new int[clusterCount];
        for (int i = 0; i < xRows; i++)
            usageCounts[labels[i]]++;

        // Sort by descending usage, keep only used ones
        var inUse = Enumerable.Range(0, clusterCount)
            .Where(i => usageCounts[i] > 0)
            .OrderByDescending(i => usageCounts[i])
            .ToArray();

        LogVerbose($"Codebook size after pruning: {inUse.Length} entries ({inUse.Length / (double)clusterCount * 100:F2} % of original)");

        var pruned = new float[inUse.Length * vectorSize];
        for (int i = 0; i < inUse.Length; i++)
            Array.Copy(clusters, inUse[i] * vectorSize, pruned, i * vectorSize, vectorSize);

        return pruned;
    }

    /// <summary>
    /// Assigns each vector in X to its nearest cluster center (brute force, Euclidean distance).
    /// </summary>
    public static void NearestNeighborAssign(
        float[] X, int xRows,
        float[] centers, int numCenters,
        int vectorSize,
        int[] outLabels)
    {
        Parallel.For(0, xRows, i =>
        {
            int bestIdx = 0;
            float bestDist = float.MaxValue;
            int xOffset = i * vectorSize;

            for (int c = 0; c < numCenters; c++)
            {
                float dist = SquaredDistance(X, xOffset, centers, c * vectorSize, vectorSize);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = c;
                }
            }
            outLabels[i] = bestIdx;
        });
    }

    /// <summary>
    /// The main encoding pipeline: reads frames, clusters blocks, writes codebooks and frames.
    /// </summary>
    public static void EncodeVideo(
        string[] framePaths, double fps, Config cfg, Encoder encoder)
    {
        var firstTestFrame = LoadAndPrepareFrame(framePaths[0], cfg);
        int blocksPerFrame = firstTestFrame.BlocksPerFrame;
        int numFrames = framePaths.Length;

        var header = new VideoHeader(
            firstTestFrame.Width, firstTestFrame.Height,
            numFrames, fps, cfg);

        int blocksY = header.Height / cfg.BlockH;
        int blocksX = header.Width / cfg.BlockW;
        int vectorSize = cfg.VectorSize;

        encoder.WriteHeader(header);

        var encodeStartTime = Stopwatch.StartNew();

        int frameIdx = 0;
        int framesWritten = 0;

        var errorHistory = new List<double>();
        var inputQueue = new Dictionary<int, RawFrame>();

        void LogInfo(string msg) =>
            Console.WriteLine($"[{framesWritten}/{framePaths.Length}, {frameIdx} read] {msg}");

        while (framesWritten < framePaths.Length)
        {
            // Prune old frames from the input queue
            var keysToRemove = inputQueue.Keys.Where(k => k < frameIdx - 1).ToList();
            foreach (var key in keysToRemove)
                inputQueue.Remove(key);

            LogInfo("Reading source images");

            // Prefetch upcoming frames in parallel
            int prefetchEnd = Math.Min(frameIdx + cfg.MaxKeyframeDistance, framePaths.Length);
            var prefetchTasks = new Dictionary<int, Task<RawFrame>>();
            for (int fi = frameIdx; fi < prefetchEnd; fi++)
            {
                if (!inputQueue.ContainsKey(fi))
                {
                    int capturedFi = fi;
                    prefetchTasks[capturedFi] = Task.Run(() => LoadAndPrepareFrame(framePaths[capturedFi], cfg));
                }
            }

            var outputQueue = new List<(RawFrame raw, float[] Xi)>();

            while (outputQueue.Count < cfg.MaxKeyframeDistance && frameIdx < framePaths.Length)
            {
                RawFrame raw;
                if (inputQueue.TryGetValue(frameIdx, out var cached))
                {
                    raw = cached;
                }
                else if (prefetchTasks.TryGetValue(frameIdx, out var task))
                {
                    raw = task.Result;
                    inputQueue[frameIdx] = raw;
                }
                else
                {
                    raw = LoadAndPrepareFrame(framePaths[frameIdx], cfg);
                    inputQueue[frameIdx] = raw;
                }

                if (inputQueue.TryGetValue(frameIdx - 1, out var lastRaw))
                {
                    double mseYuv = ComputeMse(lastRaw.Yuv, raw.Yuv);
                    errorHistory.Add(mseYuv);

                    double runningError;
                    if (outputQueue.Count >= cfg.MinKeyframeDistance)
                    {
                        errorHistory.RemoveAt(0);
                        runningError = Math.Max(1e-6, errorHistory.Average());
                    }
                    else
                    {
                        runningError = 1e9;
                    }

                    double mseRatio = mseYuv / runningError;
                    LogVerbose($"MSE to last = {mseYuv:F3}, running MSE: {runningError:F3}, ratio: {mseRatio:F4}");

                    bool isFirstFrameOfWindow = outputQueue.Count == 0;
                    if (mseRatio > cfg.SceneCutErrorLimit && !isFirstFrameOfWindow)
                    {
                        LogInfo($"Scene cut {mseYuv:F4} > {runningError:F4}!");
                        break;
                    }
                }

                var Xi = new float[blocksPerFrame * vectorSize];
                BlockOps.SplitFrame(raw.Yuv, raw.Width, cfg.BlockW, cfg.BlockH, Xi, blocksY, blocksX);
                outputQueue.Add((raw, Xi));

                frameIdx++;
            }

            // Cache any completed prefetch results for reuse after scene cuts
            foreach (var kvp in prefetchTasks)
            {
                if (!inputQueue.ContainsKey(kvp.Key) && kvp.Value.IsCompleted && !kvp.Value.IsFaulted)
                    inputQueue[kvp.Key] = kvp.Value.Result;
            }

            // Build the big X matrix with all visible blocks
            var X = new float[blocksPerFrame * outputQueue.Count * vectorSize];

            var firstXi = outputQueue[0].Xi;
            // Reshape Xi into [blocksY, blocksX, vectorSize] — stored flat

            var framebuffer = new float[firstXi.Length];
            Array.Copy(firstXi, framebuffer, firstXi.Length);
            var accumFrameError = new double[blocksY * blocksX];
            var frameUpdateMasks = new bool[outputQueue.Count][]; // [frame][blocksY * blocksX]
            for (int f = 0; f < outputQueue.Count; f++)
                frameUpdateMasks[f] = new bool[blocksY * blocksX];

            // Always write the first frame fully
            Array.Copy(firstXi, 0, X, 0, blocksPerFrame * vectorSize);
            Array.Fill(frameUpdateMasks[0], true);
            int numVisibleBlocks = blocksPerFrame;

            for (int i = 1; i < outputQueue.Count; i++)
            {
                var Xi = outputQueue[i].Xi;

                // Compute per-block MSE between Xi and framebuffer
                for (int b = 0; b < blocksPerFrame; b++)
                {
                    double error = 0;
                    int bOffset = b * vectorSize;
                    for (int d = 0; d < vectorSize; d++)
                    {
                        double diff = Xi[bOffset + d] - framebuffer[bOffset + d];
                        error += diff * diff;
                    }
                    error /= vectorSize;
                    accumFrameError[b] += error;

                    if (accumFrameError[b] > cfg.BlockReplacementError)
                    {
                        frameUpdateMasks[i][b] = true;
                        Array.Copy(Xi, bOffset, X, numVisibleBlocks * vectorSize, vectorSize);
                        numVisibleBlocks++;
                        Array.Copy(Xi, bOffset, framebuffer, bOffset, vectorSize);
                        accumFrameError[b] = 0;
                    }
                }
            }

            // Trim X to actual size
            var Xtrimmed = new float[numVisibleBlocks * vectorSize];
            Array.Copy(X, Xtrimmed, numVisibleBlocks * vectorSize);

            LogInfo($"Fitting {cfg.NumBlocks} clusters to [{numVisibleBlocks}, {vectorSize}] array");
            var clusterStart = Stopwatch.StartNew();
            var clusters = FitClusters(
                Xtrimmed, numVisibleBlocks, vectorSize,
                cfg.NumBlocks, cfg.MaxVectorsToFit,
                deduplicate: true, seed: framesWritten);
            LogInfo($"Took {clusterStart.Elapsed.TotalSeconds:F4} s");

            int clusterCount = clusters.Length / vectorSize;
            clusters = SortAndPruneClusters(Xtrimmed, numVisibleBlocks, clusters, clusterCount, vectorSize);
            clusterCount = clusters.Length / vectorSize;

            var codebook = new Codebook(clusters, clusterCount, vectorSize);
            encoder.WriteCodebook(codebook);

            // Assign blocks to nearest codebook entry and write frames
            var labels = new int[blocksPerFrame];
            for (int i = 0; i < outputQueue.Count; i++)
            {
                var (raw, Xi) = outputQueue[i];

                NearestNeighborAssign(Xi, blocksPerFrame, clusters, clusterCount, vectorSize, labels);

                var indices = new int[blocksY, blocksX];
                var staleBlocks = new bool[blocksY, blocksX];

                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int flatIdx = by * blocksX + bx;
                        staleBlocks[by, bx] = frameUpdateMasks[i][flatIdx];
                        indices[by, bx] = staleBlocks[by, bx] ? labels[flatIdx] : -1;
                    }
                }

                int numStale = frameUpdateMasks[i].Count(v => v);
                LogInfo($"Writing a frame with {numStale} ({numStale / (double)(blocksPerFrame) * 100:F2} %) changed blocks");

                encoder.WriteFrame(indices, staleBlocks);

                framesWritten++;
            }

            double elapsed = encodeStartTime.Elapsed.TotalSeconds;
            double secsPerFrame = elapsed / framesWritten;
            double remaining = secsPerFrame * (numFrames - framesWritten);
            LogInfo($"{(int)(remaining / 60):D2}:{(int)remaining % 60:D2} remaining");
        }

        encoder.FinalizeOutput();

        double totalTook = encodeStartTime.Elapsed.TotalSeconds;
        Console.Write($"Encoding {numFrames} frames took {(int)(totalTook / 60)} m {(int)totalTook % 60} s, ");
        Console.WriteLine($"{totalTook / numFrames:F3} s per frame on average");
    }

    // ---- Internal helpers ----

    private static double ComputeMse(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum / a.Length;
    }

    /// <summary>
    /// Deduplicates rows in a flat vector array. Returns (uniqueData, counts, numUnique).
    /// </summary>
    private static (float[] data, int[] counts, int numUnique) DeduplicateVectors(
        float[] X, int numRows, int vectorSize)
    {
        // Use a dictionary keyed by row content
        var dict = new Dictionary<VectorKey, int>(); // key -> index in unique list
        var uniqueList = new List<int>(); // original row indices
        var countsList = new List<int>();

        for (int i = 0; i < numRows; i++)
        {
            var key = new VectorKey(X, i * vectorSize, vectorSize);
            if (dict.TryGetValue(key, out int existing))
            {
                countsList[existing]++;
            }
            else
            {
                dict[key] = uniqueList.Count;
                uniqueList.Add(i);
                countsList.Add(1);
            }
        }

        int numUnique = uniqueList.Count;
        var data = new float[numUnique * vectorSize];
        for (int i = 0; i < numUnique; i++)
            Array.Copy(X, uniqueList[i] * vectorSize, data, i * vectorSize, vectorSize);

        return (data, countsList.ToArray(), numUnique);
    }

    /// <summary>
    /// A hashable key for a float vector (used for deduplication).
    /// </summary>
    private readonly struct VectorKey : IEquatable<VectorKey>
    {
        private readonly float[] _source;
        private readonly int _offset;
        private readonly int _length;
        private readonly int _hash;

        public VectorKey(float[] source, int offset, int length)
        {
            _source = source;
            _offset = offset;
            _length = length;

            // FNV-1a inspired hash
            unchecked
            {
                int h = (int)2166136261;
                for (int i = offset; i < offset + length; i++)
                {
                    h ^= BitConverter.SingleToInt32Bits(source[i]);
                    h *= 16777619;
                }
                _hash = h;
            }
        }

        public override int GetHashCode() => _hash;

        public bool Equals(VectorKey other)
        {
            if (_length != other._length) return false;
            for (int i = 0; i < _length; i++)
            {
                if (_source[_offset + i] != other._source[other._offset + i])
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is VectorKey other && Equals(other);
    }

    // ---- Mini-Batch K-Means ----

    static float SquaredDistance(float[] a, int aOffset, float[] b, int bOffset, int length)
    {
        int simdLength = Vector<float>.Count;
        int d = 0;
        var sumVec = Vector<float>.Zero;

        for (; d <= length - simdLength; d += simdLength)
        {
            var va = new Vector<float>(a, aOffset + d);
            var vb = new Vector<float>(b, bOffset + d);
            var diff = va - vb;
            sumVec += diff * diff;
        }

        float dist = Vector.Dot(sumVec, Vector<float>.One);
        for (; d < length; d++)
        {
            float diff = a[aOffset + d] - b[bOffset + d];
            dist += diff * diff;
        }
        return dist;
    }

    /// <summary>
    /// A simple Mini-Batch K-Means implementation.
    /// </summary>
    private static float[] MiniBatchKMeans(
        float[] data, int numRows, int vectorSize,
        int k, int[]? sampleWeights, int seed,
        int nInit = 3, int maxIter = 100, int batchSize = 1024)
    {
        var rng = new Random(seed);
        float[] bestCenters = new float[k * vectorSize];
        double bestInertia = double.MaxValue;

        for (int init = 0; init < nInit; init++)
        {
            Console.WriteLine("MiniBatchKMeans - iteration " + init);

            // Random initialization: pick k random data points as initial centers
            var centers = new float[k * vectorSize];
            var chosen = new HashSet<int>();
            for (int c = 0; c < k; c++)
            {
                int idx;
                do { idx = rng.Next(numRows); } while (!chosen.Add(idx));
                Array.Copy(data, idx * vectorSize, centers, c * vectorSize, vectorSize);
            }

            var centerCounts = new double[k];

            for (int iter = 0; iter < maxIter; iter++)
            {
                // Sample a mini-batch
                int actualBatch = Math.Min(batchSize, numRows);
                var batchIndices = new int[actualBatch];
                for (int i = 0; i < actualBatch; i++)
                    batchIndices[i] = rng.Next(numRows);

                // Assign each batch point to nearest center
                var assignments = new int[actualBatch];
                Parallel.For(0, actualBatch, i =>
                {
                    int dataOffset = batchIndices[i] * vectorSize;
                    float bestDist = float.MaxValue;
                    int bestC = 0;
                    for (int c = 0; c < k; c++)
                    {
                        float dist = SquaredDistance(data, dataOffset, centers, c * vectorSize, vectorSize);
                        if (dist < bestDist) { bestDist = dist; bestC = c; }
                    }
                    assignments[i] = bestC;
                });

                // Update centers using streaming average
                for (int i = 0; i < actualBatch; i++)
                {
                    int c = assignments[i];
                    double weight = sampleWeights != null ? sampleWeights[batchIndices[i]] : 1.0;
                    centerCounts[c] += weight;
                    double eta = weight / centerCounts[c];
                    int cOffset = c * vectorSize;
                    int dataOffset = batchIndices[i] * vectorSize;
                    for (int d = 0; d < vectorSize; d++)
                    {
                        centers[cOffset + d] += (float)(eta * (data[dataOffset + d] - centers[cOffset + d]));
                    }
                }
            }

            // Compute inertia (total squared distance)
            // double inertia = 0;
            // for (int i = 0; i < numRows; i++)
            // {
            //     float bestDist = float.MaxValue;
            //     int xOffset = i * vectorSize;
            //     for (int c = 0; c < k; c++)
            //     {
            //         float dist = 0;
            //         int cOffset = c * vectorSize;
            //         for (int d = 0; d < vectorSize; d++)
            //         {
            //             float diff = data[xOffset + d] - centers[cOffset + d];
            //             dist += diff * diff;
            //         }
            //         if (dist < bestDist) bestDist = dist;
            //     }
            //     double w = sampleWeights != null ? sampleWeights[i] : 1.0;
            //     inertia += bestDist * w;
            // }

            Console.WriteLine("Computing inertia...");

            double inertia = 0;
            var partialSums = new double[Environment.ProcessorCount];

            Parallel.For(0, numRows, () => 0.0, (i, state, localSum) =>
            {
                float bestDist = float.MaxValue;
                int xOffset = i * vectorSize;
                for (int c = 0; c < k; c++)
                {
                    float dist = SquaredDistance(data, xOffset, centers, c * vectorSize, vectorSize);
                    if (dist < bestDist) bestDist = dist;
                }
                double w = sampleWeights != null ? sampleWeights[i] : 1.0;
                return localSum + bestDist * w;
            },
            localSum => { lock (partialSums) { inertia += localSum; } });

            if (inertia < bestInertia)
            {
                bestInertia = inertia;
                Array.Copy(centers, bestCenters, centers.Length);
            }
        }

        LogVerbose($"K-Means completed with inertia {bestInertia:F2}");
        return bestCenters;
    }
}
