using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Filters;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Toolboxs.FilterTools;
using Sdcb.FFmpeg.Utils;

namespace FluentFMPEG
{
    internal sealed record EngineProgress(double Percent);

    internal sealed record OutputPlan
    {
        public required Mode Mode { get; init; }
        public required string MuxerFormat { get; init; }
        public required PresetCategory Category { get; init; }
        public VideoSpec? Video { get; init; }
        public AudioSpec? Audio { get; init; }
        public bool VideoCopy { get; init; }                // Mix mode: stream-copy primary video
        public bool SingleFrame { get; init; }              // Image presets: write 1 frame and stop
        public Dictionary<string, string>? MuxerOptions { get; init; }
    }

    internal sealed record VideoSpec
    {
        public required string EncoderName { get; init; }
        public Dictionary<string, string>? PrivateOptions { get; init; }
        public long Bitrate { get; init; }
        public int? GlobalQuality { get; init; }
        public AVPixelFormat? PreferredPixelFormat { get; init; }
        // Optional FOURCC override (e.g., "hvc1" for HEVC-in-MP4 Apple compat).
        public uint? CodecTag { get; init; }
        // Optional integer profile for the encoder (set via AVCodecContext.profile,
        // e.g., ProRes profile 3 = HQ).
        public int? Profile { get; init; }
    }

    internal sealed record AudioSpec
    {
        public required string EncoderName { get; init; }
        public Dictionary<string, string>? PrivateOptions { get; init; }
        public long Bitrate { get; init; }
    }

    internal static class ConvertEngine
    {
        // Sentinel exit code so we can signal cancellation back to OnConvertClick
        // without throwing OperationCanceledException — the latter triggers VS's
        // user-unhandled-exception break even though it'd be caught one frame up.
        public const int CanceledExitCode = -2;

        // Sdcb.FFmpeg's struct types make extensive use of Nullable<T>; the helpers
        // below hide that ergonomic pain.
        private const AV_BUFFERSINK_FLAG SinkBlocking = (AV_BUFFERSINK_FLAG)0;
        private const AV_BUFFERSRC_FLAG SrcDefault = AV_BUFFERSRC_FLAG.Default;

        public static Task<int> RunAsync(
            string primaryInput,
            string? secondaryInput,
            string outputPath,
            OutputPlan plan,
            AdvancedSettings adv,
            IProgress<EngineProgress>? progress,
            Action<string>? log,
            CancellationToken ct) =>
            // Note: don't pass `ct` to Task.Run — we handle cancellation cooperatively
            // and return CanceledExitCode rather than throwing TaskCanceledException.
            Task.Run(() => Run(primaryInput, secondaryInput, outputPath, plan, adv, progress, log, ct));

        private static int Run(
            string primaryInput,
            string? secondaryInput,
            string outputPath,
            OutputPlan plan,
            AdvancedSettings adv,
            IProgress<EngineProgress>? progress,
            Action<string>? log,
            CancellationToken ct)
        {
            log?.Invoke($"Opening input: {Path.GetFileName(primaryInput)}");
            using var inA = FormatContext.OpenInputUrl(primaryInput);
            inA.LoadStreamInfo();

            FormatContext? inB = null;
            FormatContext? outFc = null;
            IOContext? outIo = null;
            CodecContext? videoDec = null;
            CodecContext? videoEnc = null;
            CodecContext? audioDec = null;
            CodecContext? audioEnc = null;
            VideoFilterContext? videoFilter = null;
            AudioFilterContext? audioFilter = null;

            try
            {
                if (secondaryInput != null)
                {
                    log?.Invoke($"Opening secondary input: {Path.GetFileName(secondaryInput)}");
                    inB = FormatContext.OpenInputUrl(secondaryInput);
                    inB.LoadStreamInfo();
                }

                MediaStream? videoIn = null;
                MediaStream? audioIn = null;
                if (plan.Mode == Mode.Mix)
                {
                    videoIn = FindFirstStream(inA, AVMediaType.Video);
                    if (inB != null) audioIn = FindFirstStream(inB, AVMediaType.Audio);
                }
                else
                {
                    if (plan.Video != null || plan.VideoCopy)
                        videoIn = FindFirstStream(inA, AVMediaType.Video);
                    if (plan.Audio != null && plan.Mode != Mode.ExtractVideo)
                        audioIn = FindFirstStream(inA, AVMediaType.Audio);
                }

                double totalDuration = inA.Duration > 0 ? inA.Duration / (double)ffmpeg.AV_TIME_BASE : 0;
                if (inB != null && plan.Mode == Mode.Mix && inB.Duration > 0)
                {
                    var dB = inB.Duration / (double)ffmpeg.AV_TIME_BASE;
                    totalDuration = totalDuration > 0 ? Math.Min(totalDuration, dB) : dB;
                }

                double trimStart = ParseTime(adv.TrimStart);
                double trimEnd = ParseTime(adv.TrimEnd);

                outFc = FormatContext.AllocOutput(null, plan.MuxerFormat, outputPath);
                bool muxerNeedsGlobalHeader = ((outFc.OutputFormat?.Flags ?? (AVFMT)0) & AVFMT.Globalheader) != 0;

                MediaStream? videoOutStream = null;

                if (videoIn is { } vIn)
                {
                    if (plan.VideoCopy)
                    {
                        log?.Invoke($"Video: stream copy ({vIn.Codecpar!.Width}x{vIn.Codecpar.Height})");
                        var vOut = outFc.NewStream(null);
                        vOut.Codecpar!.CopyFrom(vIn.Codecpar!);
                        vOut.Codecpar.CodecTag = 0;
                        vOut.TimeBase = vIn.TimeBase;
                        videoOutStream = vOut;
                    }
                    else if (plan.Video != null)
                    {
                        videoDec = OpenDecoder(vIn);

                        var encoderOpt = Codec.FindEncoderByName(plan.Video.EncoderName);
                        if (encoderOpt is not { } encoder)
                            throw new InvalidOperationException($"Encoder '{plan.Video.EncoderName}' not available in this libavcodec build.");

                        videoEnc = new CodecContext(encoder);

                        var sinkPixFmt = plan.Video.PreferredPixelFormat
                            ?? FirstSupportedPixelFormat(encoder)
                            ?? AVPixelFormat.Yuv420p;

                        string filterChain = BuildVideoFilter(adv, sinkPixFmt);
                        videoFilter = VideoFilterContext.Create(vIn, filterChain, sinkPixFmt);
                        videoFilter.ConfigureEncoder(videoEnc);

                        if (plan.Video.Bitrate > 0) videoEnc.BitRate = plan.Video.Bitrate;
                        if (plan.Video.GlobalQuality is { } gq)
                        {
                            videoEnc.Flags |= AV_CODEC_FLAG.Qscale;
                            videoEnc.GlobalQuality = gq * ffmpeg.FF_QP2LAMBDA;
                        }
                        if (plan.Video.Profile is { } profile)
                            videoEnc.Profile = profile;
                        if (muxerNeedsGlobalHeader)
                            videoEnc.Flags |= AV_CODEC_FLAG.GlobalHeader;

                        videoEnc.Open(encoder, ToDict(plan.Video.PrivateOptions));
                        log?.Invoke($"Video encoder: {plan.Video.EncoderName} ({videoEnc.Width}x{videoEnc.Height}, {NameUtils.GetPixelFormatName(sinkPixFmt)}) | filter: {filterChain}");

                        var vOut = outFc.NewStream(encoder);
                        vOut.TimeBase = videoEnc.TimeBase;
                        vOut.Codecpar!.CopyFrom(videoEnc);
                        if (plan.Video.CodecTag is { } tag)
                            vOut.Codecpar.CodecTag = tag;
                        videoOutStream = vOut;
                    }
                }

                MediaStream? audioOutStream = null;

                if (audioIn is { } aIn && plan.Audio != null)
                {
                    audioDec = OpenDecoder(aIn);

                    var encoderOpt = Codec.FindEncoderByName(plan.Audio.EncoderName);
                    if (encoderOpt is not { } encoder)
                        throw new InvalidOperationException($"Encoder '{plan.Audio.EncoderName}' not available in this libavcodec build.");

                    audioEnc = new CodecContext(encoder);

                    var sinkParams = BuildAudioSinkParams(encoder, audioDec, adv);
                    audioFilter = AudioFilterContext.Create(aIn, "anull", sinkParams);
                    audioFilter.ConfigureEncoder(audioEnc);

                    if (plan.Audio.Bitrate > 0) audioEnc.BitRate = plan.Audio.Bitrate;
                    if (muxerNeedsGlobalHeader)
                        audioEnc.Flags |= AV_CODEC_FLAG.GlobalHeader;

                    audioEnc.Open(encoder, ToDict(plan.Audio.PrivateOptions));
                    log?.Invoke($"Audio encoder: {plan.Audio.EncoderName} ({audioEnc.SampleRate} Hz, {NameUtils.GetSampleFormatName(audioEnc.SampleFormat)}, {audioEnc.ChLayout.nb_channels}ch{(plan.Audio.Bitrate > 0 ? $", {plan.Audio.Bitrate / 1000} kbps" : "")})");

                    var aOut = outFc.NewStream(encoder);
                    aOut.TimeBase = audioEnc.TimeBase;
                    aOut.Codecpar!.CopyFrom(audioEnc);
                    audioOutStream = aOut;

                    if (!encoder.Capabilities.HasFlag(AV_CODEC_CAP.VariableFrameSize)
                        && audioEnc.FrameSize > 0)
                    {
                        audioFilter.SinkContext.SetFrameSize(audioEnc.FrameSize);
                    }
                }

                outIo = IOContext.OpenWrite(outputPath, null);
                outFc.Pb = outIo;
                outFc.WriteHeader(ToDict(plan.MuxerOptions));
                log?.Invoke($"Muxer: {plan.MuxerFormat} → {Path.GetFileName(outputPath)}");

                if (trimStart > 0)
                {
                    long ts = (long)(trimStart * ffmpeg.AV_TIME_BASE);
                    try { inA.SeekFrame(ts, -1, AVSEEK_FLAG.Backward); } catch { }
                    if (inB != null) try { inB.SeekFrame(ts, -1, AVSEEK_FLAG.Backward); } catch { }
                }

                using var workFrame = new Frame();
                using var filtFrame = new Frame();
                using var encPacket = new Packet();

                int videoFramesEncoded = 0;
                long audioSamplesEncoded = 0;
                int packetsRead = 0;
                long lastProgressTick = 0;
                long lastLogTick = 0;
                long startTick = Environment.TickCount64;

                void WriteEncoded(CodecContext enc, MediaStream outStream, IEnumerable<Packet> packets)
                {
                    foreach (var pkt in packets)
                    {
                        pkt.RescaleTimestamp(enc.TimeBase, outStream.TimeBase);
                        pkt.StreamIndex = outStream.Index;
                        outFc.InterleavedWritePacket(pkt);
                    }
                }

                // Report progress on the *input* side: we know how far through the file
                // we've read, even if the encoder is still buffering frames internally.
                // This avoids the long initial pause for high-latency encoders (VP9, x265).
                void ReportInputProgress(long pts, AVRational tb)
                {
                    if (progress == null || totalDuration <= 0) return;
                    if (pts == ffmpeg.AV_NOPTS_VALUE) return;
                    double t = pts * tb.Num / (double)tb.Den;
                    double pct = Math.Clamp(t / totalDuration * 100.0, 0, 100);
                    long now = Environment.TickCount64;
                    if (now - lastProgressTick > 200)
                    {
                        lastProgressTick = now;
                        progress.Report(new EngineProgress(pct));
                    }
                }

                void MaybeLogStatus(double inputSecs)
                {
                    if (log == null) return;
                    long now = Environment.TickCount64;
                    if (now - lastLogTick < 1500) return;
                    lastLogTick = now;
                    double elapsed = (now - startTick) / 1000.0;
                    string position = totalDuration > 0
                        ? $"{FormatHms(inputSecs)}/{FormatHms(totalDuration)}"
                        : FormatHms(inputSecs);
                    string speed = elapsed > 0.1 && inputSecs > 0
                        ? $" ({inputSecs / elapsed:0.00}x)"
                        : "";
                    log($"  {position}{speed}  v={videoFramesEncoded} frames, a={audioSamplesEncoded} samples");
                }

                IEnumerable<Packet> packetSource = inB != null
                    ? InterleavePackets(inA, inB, videoIn, audioIn)
                    : inA.ReadPackets();

                log?.Invoke("Encoding…");

                bool earlyStop = false;
                foreach (var pkt in packetSource)
                {
                    if (ct.IsCancellationRequested)
                    {
                        pkt.Unref();
                        earlyStop = true;
                        break;
                    }
                    packetsRead++;

                    bool consumed = false;
                    long pktPts = pkt.Pts;
                    AVRational pktTb = default;

                    if (videoIn is { } vS && pkt.StreamIndex == vS.Index && videoOutStream is { } vOut)
                    {
                        pktTb = vS.TimeBase;
                        if (plan.VideoCopy)
                        {
                            pkt.RescaleTimestamp(vS.TimeBase, vOut.TimeBase);
                            pkt.StreamIndex = vOut.Index;
                            outFc.InterleavedWritePacket(pkt);
                            videoFramesEncoded++;
                        }
                        else if (videoDec != null && videoEnc != null && videoFilter != null)
                        {
                            foreach (var frame in videoDec.DecodePacket(pkt, workFrame, unref: true))
                            {
                                if (ct.IsCancellationRequested) { earlyStop = true; break; }
                                if (!FrameInTrim(frame, vS.TimeBase, trimStart, trimEnd)) continue;

                                videoFilter.SourceContext.WriteFrame(frame);
                                while (videoFilter.SinkContext.GetFrame(filtFrame, SinkBlocking) >= 0)
                                {
                                    try
                                    {
                                        WriteEncoded(videoEnc, vOut,
                                            videoEnc.EncodeFrame(filtFrame, encPacket, unref: true));
                                        videoFramesEncoded++;
                                        if (plan.SingleFrame && videoFramesEncoded >= 1)
                                        {
                                            earlyStop = true;
                                            break;
                                        }
                                    }
                                    finally { filtFrame.Unref(); }
                                    if (ct.IsCancellationRequested) { earlyStop = true; break; }
                                }
                                if (earlyStop) break;
                            }
                        }
                        consumed = true;
                    }
                    else if (audioIn is { } aS && pkt.StreamIndex == aS.Index && audioOutStream is { } aOut
                             && audioDec != null && audioEnc != null && audioFilter != null)
                    {
                        pktTb = aS.TimeBase;
                        foreach (var frame in audioDec.DecodePacket(pkt, workFrame, unref: true))
                        {
                            if (ct.IsCancellationRequested) { earlyStop = true; break; }
                            if (!FrameInTrim(frame, aS.TimeBase, trimStart, trimEnd)) continue;

                            audioFilter.SourceContext.WriteFrame(frame);
                            while (audioFilter.SinkContext.GetFrame(filtFrame, SinkBlocking) >= 0)
                            {
                                try
                                {
                                    WriteEncoded(audioEnc, aOut,
                                        audioEnc.EncodeFrame(filtFrame, encPacket, unref: true));
                                    audioSamplesEncoded += filtFrame.NbSamples;
                                }
                                finally { filtFrame.Unref(); }
                                if (ct.IsCancellationRequested) { earlyStop = true; break; }
                            }
                            if (earlyStop) break;
                        }
                        consumed = true;
                    }

                    if (consumed && pktPts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        ReportInputProgress(pktPts, pktTb);
                        double inputSecs = pktPts * pktTb.Num / (double)pktTb.Den;
                        MaybeLogStatus(inputSecs);
                    }

                    if (!consumed) pkt.Unref();
                    if (earlyStop) break;
                }

                // Cancellation: skip the (potentially slow) drain + trailer entirely.
                // The finally block below releases all native resources.
                if (ct.IsCancellationRequested)
                {
                    log?.Invoke("[cancel] Aborted before drain.");
                    return CanceledExitCode;
                }

                // Drain video pipeline.
                if (videoEnc != null && videoFilter != null && videoOutStream is { } vOutFinal && !plan.VideoCopy)
                {
                    videoFilter.SourceContext.CloseBufferSource(0, SrcDefault);
                    while (videoFilter.SinkContext.GetFrame(filtFrame, SinkBlocking) >= 0)
                    {
                        try
                        {
                            WriteEncoded(videoEnc, vOutFinal,
                                videoEnc.EncodeFrame(filtFrame, encPacket, unref: true));
                        }
                        finally { filtFrame.Unref(); }
                    }
                    WriteEncoded(videoEnc, vOutFinal,
                        videoEnc.EncodeFrame(null, encPacket, unref: true));
                }

                // Drain audio pipeline.
                if (audioEnc != null && audioFilter != null && audioOutStream is { } aOutFinal)
                {
                    audioFilter.SourceContext.CloseBufferSource(0, SrcDefault);
                    while (audioFilter.SinkContext.GetFrame(filtFrame, SinkBlocking) >= 0)
                    {
                        try
                        {
                            WriteEncoded(audioEnc, aOutFinal,
                                audioEnc.EncodeFrame(filtFrame, encPacket, unref: true));
                        }
                        finally { filtFrame.Unref(); }
                    }
                    WriteEncoded(audioEnc, aOutFinal,
                        audioEnc.EncodeFrame(null, encPacket, unref: true));
                }

                outFc.WriteTrailer();

                double totalElapsed = (Environment.TickCount64 - startTick) / 1000.0;
                log?.Invoke($"Wrote {packetsRead} input packets, {videoFramesEncoded} video frames, {audioSamplesEncoded} audio samples in {totalElapsed:0.0}s");
                progress?.Report(new EngineProgress(100));
                return 0;
            }
            finally
            {
                // Free encoders/filters first (they reference codec params), then
                // close the output IO so the file handle is released back to the OS,
                // and finally dispose the format context. Any of these can be null
                // if we failed early in setup — null-conditional handles that.
                try { videoFilter?.FilterGraph.Free(); } catch { }
                try { audioFilter?.FilterGraph.Free(); } catch { }
                try { videoEnc?.Free(); } catch { }
                try { audioEnc?.Free(); } catch { }
                try { videoDec?.Free(); } catch { }
                try { audioDec?.Free(); } catch { }

                // The output file handle lives in IOContext, separate from
                // FormatContext — closing it explicitly is what lets other programs
                // open/modify the file once we're done.
                if (outFc != null) outFc.Pb = null;
                try { outIo?.Close(); } catch { }
                try { outFc?.Dispose(); } catch { }
                try { inB?.Dispose(); } catch { }
            }
        }

        private static MediaStream? FindFirstStream(FormatContext fc, AVMediaType type)
        {
            foreach (var s in fc.Streams)
            {
                if (s.Codecpar is { } par && par.CodecType == type)
                    return s;
            }
            return null;
        }

        private static CodecContext OpenDecoder(MediaStream stream)
        {
            var codec = Codec.FindDecoderById(stream.Codecpar!.CodecId);
            var ctx = new CodecContext(codec);
            ctx.FillParameters(stream.Codecpar);
            ctx.Open(codec, null);
            return ctx;
        }

        private static AVPixelFormat? FirstSupportedPixelFormat(Codec encoder)
        {
            var first = encoder.PixelFormats?.FirstOrDefault() ?? AVPixelFormat.None;
            return first == AVPixelFormat.None ? null : first;
        }

        private static AVSampleFormat? FirstSupportedSampleFormat(Codec encoder)
        {
            var first = encoder.SampleFormats?.FirstOrDefault() ?? AVSampleFormat.None;
            return first == AVSampleFormat.None ? null : first;
        }

        private static int? FirstSupportedSampleRate(Codec encoder)
        {
            var first = encoder.SupportedSamplerates?.FirstOrDefault() ?? 0;
            return first == 0 ? null : first;
        }

        private static string BuildVideoFilter(AdvancedSettings adv, AVPixelFormat sinkFmt)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(adv.Resolution)) parts.Add($"scale={adv.Resolution}");
            if (!string.IsNullOrEmpty(adv.FrameRate)) parts.Add($"fps={adv.FrameRate}");
            parts.Add($"format={NameUtils.GetPixelFormatName(sinkFmt)}");
            return string.Join(",", parts);
        }

        private static unsafe AudioSinkParams BuildAudioSinkParams(Codec encoder, CodecContext decoder, AdvancedSettings adv)
        {
            int sampleRate = !string.IsNullOrEmpty(adv.SampleRate) && int.TryParse(adv.SampleRate, out var sr)
                ? sr
                : (FirstSupportedSampleRate(encoder) ?? decoder.SampleRate);

            AVSampleFormat fmt = FirstSupportedSampleFormat(encoder) ?? decoder.SampleFormat;

            AVChannelLayout layout;
            if (adv.Channels is 1)
            {
                ffmpeg.av_channel_layout_default(&layout, 1);
            }
            else if (adv.Channels is 2)
            {
                ffmpeg.av_channel_layout_default(&layout, 2);
            }
            else
            {
                layout = decoder.ChLayout;
                if (layout.nb_channels == 0)
                    ffmpeg.av_channel_layout_default(&layout, 2);
            }

            return new AudioSinkParams(layout, sampleRate, fmt);
        }

        private static MediaDictionary? ToDict(Dictionary<string, string>? src)
        {
            if (src == null || src.Count == 0) return null;
            return MediaDictionary.FromDictionary(src);
        }

        private static double ParseTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.Contains(':'))
            {
                var parts = s.Split(':');
                if (parts.Length == 3
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                    return h * 3600 + m * 60 + sec;
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ss))
                    return mm * 60 + ss;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static string FormatHms(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) return "?";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes}:{ts.Seconds:00}";
        }

        private static bool FrameInTrim(Frame frame, AVRational tb, double trimStart, double trimEnd)
        {
            if (trimStart <= 0 && trimEnd <= 0) return true;
            if (frame.Pts == ffmpeg.AV_NOPTS_VALUE) return true;
            double t = frame.Pts * tb.Num / (double)tb.Den;
            if (trimStart > 0 && t < trimStart) return false;
            if (trimEnd > 0 && t > trimEnd) return false;
            return true;
        }

        private static IEnumerable<Packet> InterleavePackets(
            FormatContext a, FormatContext b, MediaStream? videoFromA, MediaStream? audioFromB)
        {
            using var enumA = a.ReadPackets().GetEnumerator();
            using var enumB = b.ReadPackets().GetEnumerator();

            bool aHas = enumA.MoveNext();
            bool bHas = enumB.MoveNext();
            while (aHas || bHas)
            {
                if (aHas)
                {
                    var pkt = enumA.Current;
                    if (videoFromA is { } vS && pkt.StreamIndex == vS.Index)
                        yield return pkt;
                    else
                        pkt.Unref();
                    aHas = enumA.MoveNext();
                }
                if (bHas)
                {
                    var pkt = enumB.Current;
                    if (audioFromB is { } aS && pkt.StreamIndex == aS.Index)
                        yield return pkt;
                    else
                        pkt.Unref();
                    bHas = enumB.MoveNext();
                }
            }
        }
    }
}
