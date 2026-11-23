# ROTH Video Compressor

ROTH is a small Windows app for making MP4s easier to send. It uses HandBrake to compress videos on your PC.

## Download

Download **ROTH-Windows-x64.zip** from [Releases](https://github.com/rooke15/roth-video-compressor/releases), extract the whole ZIP, and open `ROTH.exe`. HandBrakeCLI is included. There’s no installer or separate encoder download, and videos stay on your PC.

You’ll need 64-bit Windows 10 or 11 on an Intel or AMD processor, with .NET Framework 4.8 or newer.

## Use it

1. Drop a video into ROTH or pick one with **Choose Video**. You can select several files at once.
2. Pick **Ultra Small**, **Medium**, or **High Quality**. For more control, open **Advanced settings** and choose a resolution limit, codec, encoding speed, and either constant quality or average bitrate.
3. Click **Compress Video**. If you select several files, ROTH works on two at a time and shows progress for each.
4. Use **Play** to check the result, then **Save As…** to keep the MP4.

H.264 is a good choice when playback compatibility matters. For H.265, a slower encoding preset takes longer and usually compresses more efficiently.

## Build from source

Run `.\build.ps1` in PowerShell on 64-bit Windows. To run a source build, extract the official HandBrakeCLI ZIP into a `HandBrakeCLI` folder next to the built `ROTH.exe`.

The packaged encoder is the unmodified [HandBrakeCLI 1.11.2](https://github.com/HandBrake/HandBrake/releases/tag/1.11.2). Its license is in `HandBrakeCLI/doc`, and its source archive is attached to the same release.
