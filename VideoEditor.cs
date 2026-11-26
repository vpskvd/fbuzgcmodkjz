using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace InstantReplay;

public readonly record struct VideoCropRect(int X, int Y, int Width, int Height);

public readonly record struct VideoExportRequest(
    string SourcePath,
    string OutputPath,
    double TrimStartSeconds,
    double TrimEndSeconds,
    VideoCropRect? Crop);

public static class VideoEditor
{
    private const int MinCropPixels = 16;

    /// <summary>
    /// Builds a unique output path in the clip's category folder so Gallery scans and sorts it automatically.
    /// </summary>
    public static string BuildOutputPath(ReplayItem item)
        => BuildOutputPath(item.FolderPath, item.VideoPath);

    public static string BuildOutputPath(string folderPath, string videoPath)
    {
        bool isLegacyFolder = Path.GetFileName(folderPath).StartsWith("replay_", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(videoPath), "replay.mp4", StringComparison.OrdinalIgnoreCase);

        string targetDir = isLegacyFolder
            ? (Path.GetDirectoryName(folderPath) ?? folderPath)
            : (Path.GetDirectoryName(videoPath) ?? folderPath);

        Directory.CreateDirectory(targetDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string candidate = Path.Combine(targetDir, $"replay_{stamp}_cut.mp4");
        int n = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(targetDir, $"replay_{stamp}_cut{n}.mp4");
            n++;
        }
        return candidate;
    }

    /// <summary>
    /// Trims and optionally crops a replay into a separate MP4 file using GPU-accelerated MediaTranscoder.
    /// </summary>
    public static async Task ExportAsync(
        VideoExportRequest request,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(request.SourcePath))
            throw new FileNotFoundException("source video file was not found.", request.SourcePath);

        string outputDir = Path.GetDirectoryName(request.OutputPath)
            ?? throw new InvalidOperationException("invalid output directory.");
        Directory.CreateDirectory(outputDir);

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"ir_export_{Guid.NewGuid():N}.mp4");
        string? tempSourceCopy = null;

        try
        {
            string sourcePathToUse = request.SourcePath;
            try
            {
                await ExportWithOptionsAsync(sourcePathToUse, tempPath, request, useHardware: true, setGopProperty: true, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception firstEx)
            {
                // Retry with a temp copy if the live player / shell holds an incompatible share lock.
                if (IsSharingOrLockError(firstEx))
                {
                    tempSourceCopy = Path.Combine(Path.GetTempPath(), $"ir_src_{Guid.NewGuid():N}.mp4");
                    using (var inFs = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var outFs = new FileStream(tempSourceCopy, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await inFs.CopyToAsync(outFs, 131072, ct);
                    }
                    sourcePathToUse = tempSourceCopy;
                }

                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                Logger.Warn($"hardware export pass failed ({firstEx.Message}); retrying with software transcode.");
                await ExportWithOptionsAsync(sourcePathToUse, tempPath, request, useHardware: false, setGopProperty: false, progress, ct);
            }

            ct.ThrowIfCancellationRequested();
            TryRepairTruncatedFirstAacPacket(tempPath);
            TryOptimizeFastStart(tempPath);
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, request.OutputPath, overwrite: false);
            progress?.Report(100.0);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        finally
        {
            if (tempSourceCopy != null)
            {
                try { if (File.Exists(tempSourceCopy)) File.Delete(tempSourceCopy); } catch { }
            }
        }
    }

    private static bool IsSharingOrLockError(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            int hr = cur.HResult;
            if (cur is IOException || cur is UnauthorizedAccessException ||
                hr == unchecked((int)0x80070020) || hr == unchecked((int)0x80070021) || hr == unchecked((int)0x80070005))
                return true;
        }
        return false;
    }

    private static async Task ExportWithOptionsAsync(
        string sourcePath,
        string tempPath,
        VideoExportRequest request,
        bool useHardware,
        bool setGopProperty,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        StorageFile srcFile = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(ct);
        MediaClip probeClip = await MediaClip.CreateFromFileAsync(srcFile).AsTask(ct);
        VideoEncodingProperties srcProps = probeClip.GetVideoEncodingProperties();

        int srcW = (int)srcProps.Width;
        int srcH = (int)srcProps.Height;
        if (srcW <= 0 || srcH <= 0)
            throw new InvalidOperationException("could not read source video dimensions.");

        double totalSec = probeClip.OriginalDuration.TotalSeconds;
        if (totalSec <= 0.1)
            throw new InvalidOperationException("video clip is too short to edit.");

        double startSec = Math.Clamp(request.TrimStartSeconds, 0.0, Math.Max(0.0, totalSec - 0.15));
        double endSec = Math.Clamp(request.TrimEndSeconds, startSec + 0.15, totalSec);

        int outW = SnapEven(srcW, MinCropPixels, srcW);
        int outH = SnapEven(srcH, MinCropPixels, srcH);

        var transcoder = new MediaTranscoder
        {
            HardwareAccelerationEnabled = useHardware,
            AlwaysReencode = true,
            VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default,
            TrimStartTime = TimeSpan.FromSeconds(startSec),
            TrimStopTime = TimeSpan.FromSeconds(endSec),
        };

        if (request.Crop is { } rawCrop)
        {
            int cw = SnapEven(rawCrop.Width, MinCropPixels, outW);
            int ch = SnapEven(rawCrop.Height, MinCropPixels, outH);
            int cx = Math.Clamp(SnapEvenFloor(rawCrop.X), 0, Math.Max(0, outW - cw));
            int cy = Math.Clamp(SnapEvenFloor(rawCrop.Y), 0, Math.Max(0, outH - ch));

            if (cx > 0 || cy > 0 || cw < outW || ch < outH)
            {
                var fx = new VideoTransformEffectDefinition
                {
                    CropRectangle = new Rect(cx, cy, cw, ch),
                    OutputSize = new Size(cw, ch),
                    ProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default,
                };
                transcoder.AddVideoEffect(fx.ActivatableClassId, true, fx.Properties);
                outW = cw;
                outH = ch;
            }
        }

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)outW;
        profile.Video.Height = (uint)outH;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        if (probeClip.EmbeddedAudioTracks.Count == 0)
        {
            profile.Audio = null;
        }
        else
        {
            AudioEncodingProperties srcAudio = probeClip.EmbeddedAudioTracks[0].GetAudioEncodingProperties();
            uint sr = srcAudio.SampleRate > 0 ? srcAudio.SampleRate : 48_000u;
            uint ch = srcAudio.ChannelCount > 0 ? Math.Clamp(srcAudio.ChannelCount, 1u, 2u) : 2u;
            uint br = srcAudio.Bitrate > 0 ? Math.Clamp(srcAudio.Bitrate, 96_000u, 320_000u) : 192_000u;
            profile.Audio = AudioEncodingProperties.CreateAac(sr, ch, br);
        }

        double fpsVal = 60.0;
        if (srcProps.FrameRate.Numerator > 0 && srcProps.FrameRate.Denominator > 0)
        {
            double rawFps = (double)srcProps.FrameRate.Numerator / srcProps.FrameRate.Denominator;
            if (double.IsFinite(rawFps) && rawFps >= 1.0 && rawFps <= 240.0)
            {
                profile.Video.FrameRate.Numerator = srcProps.FrameRate.Numerator;
                profile.Video.FrameRate.Denominator = srcProps.FrameRate.Denominator;
                fpsVal = rawFps;
            }
        }

        if (setGopProperty)
        {
            uint gopFrames = (uint)Math.Clamp((int)Math.Round(fpsVal * 0.5), 15, 120);
            // CODECAPI_AVEncMPVGOPSize — keep keyframes every ~0.5s for instant timeline scrubbing
            profile.Video.Properties[new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1")] = gopFrames;
        }

        double areaRatio = Math.Clamp((double)(outW * outH) / (srcW * srcH), 0.05, 1.0);
        uint baseBitrate = srcProps.Bitrate > 0 ? srcProps.Bitrate : 18_000_000u;
        profile.Video.Bitrate = (uint)Math.Clamp(
            Math.Round(baseBitrate * Math.Sqrt(areaRatio)),
            2_500_000,
            50_000_000);

        using (FileStream fs = File.Create(tempPath)) { }
        StorageFile tempFile = await StorageFile.GetFileFromPathAsync(tempPath).AsTask(ct);

        PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(srcFile, tempFile, profile).AsTask(ct);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"export unsupported ({prepared.FailureReason}).");

        var transcodeOp = prepared.TranscodeAsync();
        transcodeOp.Progress = (_, pct) => progress?.Report(Math.Clamp(pct, 0.0, 96.0));
        await transcodeOp.AsTask(ct);
    }

    private static int SnapEven(int value, int min, int max)
    {
        int clamped = Math.Clamp(value, min, max);
        int even = clamped & ~1;
        if (even < min) even = (min + 1) & ~1;
        return Math.Min(even, max & ~1);
    }

    private static int SnapEvenFloor(int value) => Math.Max(0, value & ~1);

    /// <summary>
    /// Repairs trimmed MP4s written before AlwaysReencode was enabled, where MediaTranscoder sliced
    /// the start or end stereo AAC packet mid-frame (&lt; 1024 samples).
    /// Replaces damaged boundary samples in-place with an ISO AAC-LC stereo silence frame + trailing zeros.
    /// </summary>
    private static bool TryRepairTruncatedFirstAacPacket(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            List<(long Offset, int Size)> patches = LocateBrokenBoundaryAacPackets(fs);
            if (patches.Count == 0) return false;

            foreach ((long off, int sz) in patches)
            {
                fs.Position = off;
                byte[] replacement = new byte[sz];
                replacement[0] = 0x21;
                replacement[1] = 0x00;
                replacement[2] = 0x00;
                replacement[3] = 0x00;
                replacement[4] = 0x00;
                replacement[5] = 0x1C;
                fs.Write(replacement, 0, replacement.Length);
                Logger.Info($"repaired truncated AAC packet in '{Path.GetFileName(filePath)}' (offset=0x{off:x}, size={sz})");
            }
            fs.Flush(true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"aac packet repair skipped for '{Path.GetFileName(filePath)}': {ex.Message}");
            return false;
        }
    }

    private static List<(long Offset, int Size)> LocateBrokenBoundaryAacPackets(Stream fs)
    {
        var results = new List<(long Offset, int Size)>();
        long len = fs.Length;
        Span<byte> hdr = stackalloc byte[16];

        long pos = 0;
        long moovStart = -1, moovEnd = -1;
        while (TryReadBox(fs, pos, len, hdr, out string type, out long contentStart, out long boxEnd))
        {
            if (type == "moov") { moovStart = contentStart; moovEnd = boxEnd; break; }
            pos = boxEnd;
        }
        if (moovStart < 0) return results;

        long tpos = moovStart;
        while (TryReadBox(fs, tpos, moovEnd, hdr, out string type, out long cs, out long be))
        {
            if (type == "trak")
            {
                CollectSoundTrakRepairs(fs, cs, be, hdr, results);
                if (results.Count > 0) return results;
            }
            tpos = be;
        }
        return results;
    }

    private static void CollectSoundTrakRepairs(Stream fs, long trakStart, long trakEnd, Span<byte> hdr, List<(long Offset, int Size)> results)
    {
        long mdiaStart = -1, mdiaEnd = -1;
        long p = trakStart;
        while (TryReadBox(fs, p, trakEnd, hdr, out string t, out long cs, out long be))
        {
            if (t == "mdia") { mdiaStart = cs; mdiaEnd = be; break; }
            p = be;
        }
        if (mdiaStart < 0) return;

        bool isSound = false;
        long minfStart = -1, minfEnd = -1;
        p = mdiaStart;
        while (TryReadBox(fs, p, mdiaEnd, hdr, out string t, out long cs, out long be))
        {
            if (t == "hdlr" && be - cs >= 12)
            {
                byte[] hb = ReadBytes(fs, cs + 8, 4);
                isSound = hb[0] == 's' && hb[1] == 'o' && hb[2] == 'u' && hb[3] == 'n';
            }
            else if (t == "minf") { minfStart = cs; minfEnd = be; }
            p = be;
        }
        if (!isSound || minfStart < 0) return;

        long stblStart = -1, stblEnd = -1;
        p = minfStart;
        while (TryReadBox(fs, p, minfEnd, hdr, out string t, out long cs, out long be))
        {
            if (t == "stbl") { stblStart = cs; stblEnd = be; break; }
            p = be;
        }
        if (stblStart < 0) return;

        bool isStereoMp4a = false;
        bool truncatedStart = false;
        bool truncatedTail = false;
        int[]? allSizes = null;
        long[]? chunkOffsets = null;
        (uint FirstChunk, uint SamplesPerChunk)[]? stscEntries = null;

        p = stblStart;
        while (TryReadBox(fs, p, stblEnd, hdr, out string t, out long cs, out long be))
        {
            long boxLen = be - cs;
            if (t == "stsd" && boxLen >= 34)
            {
                byte[] sb = ReadBytes(fs, cs + 8, 26);
                if (sb[4] == 'm' && sb[5] == 'p' && sb[6] == '4' && sb[7] == 'a')
                {
                    ushort channels = ReadU16BE(sb.AsSpan(24, 2));
                    isStereoMp4a = channels == 2;
                }
            }
            else if (t == "stts" && boxLen >= 20)
            {
                byte[] tb = ReadBytes(fs, cs + 4, (int)Math.Min(boxLen - 4, 4 + 8 * 8));
                uint count = ReadU32BE(tb.AsSpan(0, 4));
                if (count >= 2 && tb.Length >= 4 + 16)
                {
                    uint c0 = ReadU32BE(tb.AsSpan(4, 4));
                    uint d0 = ReadU32BE(tb.AsSpan(8, 4));
                    uint d1 = ReadU32BE(tb.AsSpan(16, 4));
                    truncatedStart = c0 == 1 && d0 > 0 && d0 < 1000 && d1 == 1024;
                    if (truncatedStart && count >= 3 && tb.Length >= 4 + count * 8)
                    {
                        int lastBase = 4 + (int)(count - 1) * 8;
                        uint cLast = ReadU32BE(tb.AsSpan(lastBase, 4));
                        uint dLast = ReadU32BE(tb.AsSpan(lastBase + 4, 4));
                        truncatedTail = cLast == 1 && dLast > 0 && dLast < 1000;
                    }
                }
            }
            else if (t == "stsc" && boxLen >= 16)
            {
                byte[] sc = ReadBytes(fs, cs + 4, (int)Math.Min(boxLen - 4, 4 + 12 * 1024));
                uint entries = ReadU32BE(sc.AsSpan(0, 4));
                if (entries >= 1 && sc.Length >= 4 + entries * 12)
                {
                    stscEntries = new (uint, uint)[entries];
                    for (int i = 0; i < entries; i++)
                    {
                        int o = 4 + i * 12;
                        stscEntries[i] = (ReadU32BE(sc.AsSpan(o, 4)), ReadU32BE(sc.AsSpan(o + 4, 4)));
                    }
                }
            }
            else if (t == "stco" && boxLen >= 12)
            {
                byte[] co = ReadBytes(fs, cs + 4, (int)(boxLen - 4));
                uint count = ReadU32BE(co.AsSpan(0, 4));
                if (count >= 1 && co.Length >= 4 + count * 4)
                {
                    chunkOffsets = new long[count];
                    for (int i = 0; i < count; i++) chunkOffsets[i] = ReadU32BE(co.AsSpan(4 + i * 4, 4));
                }
            }
            else if (t == "co64" && boxLen >= 16)
            {
                byte[] co = ReadBytes(fs, cs + 4, (int)(boxLen - 4));
                uint count = ReadU32BE(co.AsSpan(0, 4));
                if (count >= 1 && co.Length >= 4 + count * 8)
                {
                    chunkOffsets = new long[count];
                    for (int i = 0; i < count; i++) chunkOffsets[i] = (long)ReadU64BE(co.AsSpan(4 + i * 8, 8));
                }
            }
            else if (t == "stsz" && boxLen >= 24)
            {
                byte[] szb = ReadBytes(fs, cs + 4, (int)(boxLen - 4));
                uint fixedSize = ReadU32BE(szb.AsSpan(0, 4));
                uint sampleCount = ReadU32BE(szb.AsSpan(4, 4));
                if (fixedSize == 0 && sampleCount >= 3 && szb.Length >= 8 + sampleCount * 4)
                {
                    allSizes = new int[sampleCount];
                    for (int i = 0; i < sampleCount; i++) allSizes[i] = (int)ReadU32BE(szb.AsSpan(8 + i * 4, 4));
                }
            }
            p = be;
        }

        if (!isStereoMp4a || !truncatedStart || allSizes == null || chunkOffsets == null || chunkOffsets.Length == 0 || stscEntries == null || stscEntries.Length == 0)
            return;

        // Check sample 0
        if (stscEntries[0].SamplesPerChunk >= 3 && allSizes[0] >= 6)
        {
            long off0 = chunkOffsets[0];
            long off1 = off0 + allSizes[0];
            long off2 = off1 + allSizes[1];
            if (off2 + 1 <= fs.Length)
            {
                int b0 = ReadByteAt(fs, off0);
                int b1 = ReadByteAt(fs, off1);
                int b2 = ReadByteAt(fs, off2);
                if (b0 >= 0 && b1 >= 0 && b2 >= 0
                    && ((b0 >> 5) & 0x7) != 1
                    && ((b1 >> 5) & 0x7) == 1
                    && ((b2 >> 5) & 0x7) == 1)
                {
                    results.Add((off0, allSizes[0]));
                }
            }
        }

        // Check tail sample if both start and end were sliced by legacy transcoder trim
        if (truncatedTail && allSizes[^1] >= 6)
        {
            long offLast = ComputeSampleOffset(allSizes.Length - 1, allSizes, chunkOffsets, stscEntries);
            if (offLast > 0 && offLast + allSizes[^1] <= fs.Length)
            {
                byte[] head6 = ReadBytes(fs, offLast, 6);
                bool alreadySilence = head6[0] == 0x21 && head6[1] == 0x00 && head6[2] == 0x00
                    && head6[3] == 0x00 && head6[4] == 0x00 && head6[5] == 0x1C;
                if (!alreadySilence)
                    results.Add((offLast, allSizes[^1]));
            }
        }
    }

    private static long ComputeSampleOffset(
        int targetSampleIndex,
        int[] sampleSizes,
        long[] chunkOffsets,
        (uint FirstChunk, uint SamplesPerChunk)[] stsc)
    {
        int si = 0;
        int stscIdx = 0;
        for (int ci = 0; ci < chunkOffsets.Length; ci++)
        {
            uint chunkNum = (uint)(ci + 1);
            while (stscIdx + 1 < stsc.Length && chunkNum >= stsc[stscIdx + 1].FirstChunk)
                stscIdx++;
            uint count = stsc[stscIdx].SamplesPerChunk;
            long offset = chunkOffsets[ci];
            for (uint k = 0; k < count && si < sampleSizes.Length; k++)
            {
                if (si == targetSampleIndex) return offset;
                offset += sampleSizes[si];
                si++;
            }
        }
        return -1;
    }

    private static bool TryReadBox(Stream fs, long pos, long limit, Span<byte> hdr, out string type, out long contentStart, out long boxEnd)
    {
        type = "";
        contentStart = 0;
        boxEnd = 0;
        if (pos + 8 > limit) return false;
        fs.Position = pos;
        if (fs.Read(hdr[..8]) < 8) return false;
        uint sz32 = ReadU32BE(hdr[..4]);
        type = System.Text.Encoding.ASCII.GetString(hdr.Slice(4, 4));
        long size = sz32;
        long headerLen = 8;
        if (sz32 == 1)
        {
            if (pos + 16 > limit || fs.Read(hdr.Slice(8, 8)) < 8) return false;
            size = (long)ReadU64BE(hdr.Slice(8, 8));
            headerLen = 16;
        }
        else if (sz32 == 0)
        {
            size = limit - pos;
        }
        if (size < headerLen || pos + size > limit) return false;
        contentStart = pos + headerLen;
        boxEnd = pos + size;
        return true;
    }

    private static byte[] ReadBytes(Stream fs, long offset, int count)
    {
        byte[] buf = new byte[count];
        fs.Position = offset;
        int total = 0;
        while (total < count)
        {
            int n = fs.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return buf;
    }

    private static int ReadByteAt(Stream fs, long offset)
    {
        fs.Position = offset;
        return fs.ReadByte();
    }

    private static ushort ReadU16BE(ReadOnlySpan<byte> b) => (ushort)((b[0] << 8) | b[1]);
    private static uint ReadU32BE(ReadOnlySpan<byte> b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    private static ulong ReadU64BE(ReadOnlySpan<byte> b) =>
        ((ulong)ReadU32BE(b[..4]) << 32) | ReadU32BE(b.Slice(4, 4));
    private static void WriteU32BE(Span<byte> b, uint val)
    {
        b[0] = (byte)(val >> 24);
        b[1] = (byte)(val >> 16);
        b[2] = (byte)(val >> 8);
        b[3] = (byte)val;
    }
    private static void WriteU64BE(Span<byte> b, ulong val)
    {
        WriteU32BE(b[..4], (uint)(val >> 32));
        WriteU32BE(b.Slice(4, 4), (uint)val);
    }

    /// <summary>
    /// Relocates the MP4 moov atom before mdat (faststart) and shifts all stco/co64 chunk offsets
    /// so browser seeking starts immediately without jumping to the end of the file first.
    /// </summary>
    private static void TryOptimizeFastStart(string filePath)
    {
        string fastPath = filePath + ".faststart";
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long len = fs.Length;
            Span<byte> hdr = stackalloc byte[16];
            var topBoxes = new List<(string Type, long Start, long End)>();
            long pos = 0;
            while (TryReadBox(fs, pos, len, hdr, out string type, out _, out long boxEnd))
            {
                topBoxes.Add((type, pos, boxEnd));
                pos = boxEnd;
            }
            if (pos != len) return;

            int mdatIdx = topBoxes.FindIndex(b => b.Type == "mdat");
            int moovIdx = topBoxes.FindIndex(b => b.Type == "moov");
            if (mdatIdx < 0 || moovIdx < 0 || moovIdx < mdatIdx) return;

            var moovBox = topBoxes[moovIdx];
            long moovLen = moovBox.End - moovBox.Start;
            if (moovLen <= 8 || moovLen > 64 * 1024 * 1024) return;

            byte[] moovBytes = ReadBytes(fs, moovBox.Start, (int)moovLen);
            if (!ShiftChunkOffsetsInMoov(moovBytes, moovLen)) return;

            using (var outFs = new FileStream(fastPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < mdatIdx; i++)
                    CopyFileRange(fs, outFs, topBoxes[i].Start, topBoxes[i].End - topBoxes[i].Start);
                outFs.Write(moovBytes, 0, moovBytes.Length);
                for (int i = mdatIdx; i < topBoxes.Count; i++)
                {
                    if (i == moovIdx) continue;
                    CopyFileRange(fs, outFs, topBoxes[i].Start, topBoxes[i].End - topBoxes[i].Start);
                }
                outFs.Flush(true);
            }

            fs.Dispose();
            File.Move(fastPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"faststart optimization skipped: {ex.Message}");
            try { if (File.Exists(fastPath)) File.Delete(fastPath); } catch { }
        }
    }

    private static bool ShiftChunkOffsetsInMoov(byte[] moov, long delta)
    {
        return ShiftBoxesRecursive(moov, 8, moov.Length, delta);
    }

    private static bool ShiftBoxesRecursive(byte[] buf, int start, int end, long delta)
    {
        int p = start;
        while (p + 8 <= end)
        {
            uint sz32 = ReadU32BE(buf.AsSpan(p, 4));
            string type = System.Text.Encoding.ASCII.GetString(buf, p + 4, 4);
            long size = sz32;
            int hdrLen = 8;
            if (sz32 == 1)
            {
                if (p + 16 > end) return false;
                size = (long)ReadU64BE(buf.AsSpan(p + 8, 8));
                hdrLen = 16;
            }
            else if (sz32 == 0)
            {
                size = end - p;
            }
            if (size < hdrLen || p + size > end) return false;
            int cs = p + hdrLen;
            int boxEnd = p + (int)size;

            if (type is "trak" or "mdia" or "minf" or "stbl" or "edts" or "udta")
            {
                if (!ShiftBoxesRecursive(buf, cs, boxEnd, delta)) return false;
            }
            else if (type == "stco" && boxEnd - cs >= 8)
            {
                uint count = ReadU32BE(buf.AsSpan(cs + 4, 4));
                if (cs + 8 + count * 4L > boxEnd) return false;
                for (int i = 0; i < count; i++)
                {
                    int off = cs + 8 + i * 4;
                    ulong updated = ReadU32BE(buf.AsSpan(off, 4)) + (ulong)delta;
                    if (updated > uint.MaxValue) return false;
                    WriteU32BE(buf.AsSpan(off, 4), (uint)updated);
                }
            }
            else if (type == "co64" && boxEnd - cs >= 8)
            {
                uint count = ReadU32BE(buf.AsSpan(cs + 4, 4));
                if (cs + 8 + count * 8L > boxEnd) return false;
                for (int i = 0; i < count; i++)
                {
                    int off = cs + 8 + i * 8;
                    ulong updated = ReadU64BE(buf.AsSpan(off, 8)) + (ulong)delta;
                    WriteU64BE(buf.AsSpan(off, 8), updated);
                }
            }

            p = boxEnd;
        }
        return true;
    }

    private static void CopyFileRange(Stream src, Stream dst, long offset, long length)
    {
        src.Position = offset;
        byte[] buffer = new byte[128 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n = src.Read(buffer, 0, toRead);
            if (n <= 0) break;
            dst.Write(buffer, 0, n);
            remaining -= n;
        }
    }
}
