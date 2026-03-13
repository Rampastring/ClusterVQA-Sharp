# C# port of ClusterVQA

This is a C# port of ClusterVQA, a **VQA Hi-Color** video encoder for *Command & Conquer: Tiberian Sun* and some other Westwood Studios' games.

The program was originally written in Python. I used an LLM and some manual work to port it to C#, and parallelized and vectorized parts of the code for a massive performance improvement.

![](docs/comparison.png)

## Features

- Encodes VQA video files that play in game
- Compresses videos with batched K-means clustering library
- Automatic scene cut detection
- Written in C# and easily hackable

## How to run it

Install .NET 8.0 and download binaries from the Releases page. Alternatively, use Visual Studio 2026 to compile the project.

Use `ffmpeg` to convert and downscale your video to a bunch of PNG files:

    ffmpeg -i inputvideo.mp4 -filter:v "scale=640:-1" "frames/frame%04d.png"

Now you should have files `frames/frame0001.png`, `frames/frame0002.png` and so on. Then you can run the encoder:

    ClusterVQA-Sharp.exe ./frames output.vqa --fps 15 --blocks 3000

## Options

To view all command-line options, run

    ClusterVQA-Sharp.exe --help

## Sound

Only PCM audio is supported. Convert your soundtrack beforehand:

    ffmpeg video.mp4 -f s16le -c:a pcm_s16le -ar 22050 audio.raw

Then use it with `--audio audio.raw` and `-ar 22050` command line arguments.

By default, ClusterVQA-Sharp encodes audio as mono. You can use the `--stereo` command line argument to encode audio as stereo. This is unlike the original ClusterVQA, which always encoded audio as stereo.

Make sure your raw input file has the same channel count and sample rate as you want ClusterVQA to output - ClusterVQA does not perform any conversion between the aidio formats, but rather applies the audio data as-is to the output VQA.

## Gotchas

- Performance is purely CPU-driven and could possibly be faster. On my system, it encodes roughly 5 frames each second at a size of 640x480 pixels.
- Only PCM audio is supported.
- Scene cut detection heuristics may produce too many keyframes.
- Westwood LCW and VPRZ compression are done with fast & loose implementations resulting in larger files.
- VQA videos with 8-bit palettes for *Command & Conquer: Red Alert* are not supported.

## Credits

Thanks to pekkavaan for the original ClusterVQA.

Thanks to CCHyper, tomsons26, OmniBlade and UGordan for VQA file format info.
Header comparison image is a frame from [*Renegade X: Tiberian Sun X*](https://www.youtube.com/watch?v=x6loeCpRBZ4) trailer by Totem Arts.
The [`rodents.png` test file](https://peach.blender.org/wp-content/uploads/rodents.png): (c) copyright 2008, Blender Foundation / www.bigbuckbunny.org and used under the Creative Commons Attribution 3.0 license. 

## License

GPLv3. See `LICENSE` file.