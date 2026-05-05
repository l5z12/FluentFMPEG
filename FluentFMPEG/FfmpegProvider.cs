using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluentFMPEG
{
    internal static class FfmpegProvider
    {
        public static string? FfmpegPath { get; private set; }
        public static string? FfprobePath { get; private set; }

        public static bool IsReady => FfmpegPath is not null && FfprobePath is not null;

        private static readonly string LocalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentFMPEG", "ffmpeg");

        private static string LocalBinDir => Path.Combine(LocalDir, "bin");
        private static string LocalFfmpeg => Path.Combine(LocalBinDir, "ffmpeg.exe");
        private static string LocalFfprobe => Path.Combine(LocalBinDir, "ffprobe.exe");

        public static async Task<bool> TryDetectAsync()
        {
            if (File.Exists(LocalFfmpeg) && File.Exists(LocalFfprobe))
            {
                FfmpegPath = LocalFfmpeg;
                FfprobePath = LocalFfprobe;
                return true;
            }
            if (await CanRunAsync("ffmpeg") && await CanRunAsync("ffprobe"))
            {
                FfmpegPath = "ffmpeg";
                FfprobePath = "ffprobe";
                return true;
            }
            return false;
        }

        private static async Task<bool> CanRunAsync(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static string AssetName()
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "winarm64",
                _ => "win64",
            };
            return $"ffmpeg-master-latest-{arch}-gpl.zip";
        }

        public sealed record DownloadProgress(
            string Status, double Percent, long BytesRead, long? TotalBytes, string? Mirror);

        // Prefix-style GitHub mirrors. Empty string = direct github.com.
        // To form a URL: mirror + "https://github.com/owner/repo/..."
        private static readonly string[] Mirrors =
        {
            "",
            "https://ghfast.top/",
            "https://github.moeyy.xyz/",
            "https://gh-proxy.com/",
            "https://ghps.cc/",
            "https://mirror.ghproxy.com/",
        };

        private static string MirrorLabel(string mirror) =>
            string.IsNullOrEmpty(mirror) ? "github.com" : new Uri(mirror).Host;

        private static async Task<string> SelectMirrorAsync(
            HttpClient http, string targetUrl, CancellationToken ct)
        {
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task<string> Probe(string mirror)
            {
                using var perCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
                perCts.CancelAfter(TimeSpan.FromSeconds(6));
                using var req = new HttpRequestMessage(HttpMethod.Get, mirror + targetUrl);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var resp = await http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, perCts.Token);
                if ((int)resp.StatusCode is >= 200 and < 300)
                    return mirror;
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            }

            var tasks = Mirrors.Select(Probe).ToList();
            Exception? last = null;
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                try
                {
                    var winner = await done;
                    raceCts.Cancel();
                    return winner;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new InvalidOperationException("No GitHub mirror responded.", last);
        }

        public static async Task DownloadAsync(
            IProgress<DownloadProgress>? progress,
            CancellationToken ct)
        {
            Directory.CreateDirectory(LocalDir);
            var asset = AssetName();
            var targetUrl = $"https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/{asset}";
            var zipPath = Path.Combine(LocalDir, asset);

            using var handler = new HttpClientHandler { AllowAutoRedirect = true };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFMPEG/1.0");

            progress?.Report(new DownloadProgress("Selecting mirror", 0, 0, null, null));
            var mirror = await SelectMirrorAsync(http, targetUrl, ct);
            var label = MirrorLabel(mirror);
            progress?.Report(new DownloadProgress("Downloading FFmpeg", 0, 0, null, label));

            using (var resp = await http.GetAsync(
                mirror + targetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;

                using var src = await resp.Content.ReadAsStreamAsync(ct);
                using var dst = File.Create(zipPath);

                var buf = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    var pct = total is > 0 ? read * 100.0 / total.Value : 0;
                    progress?.Report(new DownloadProgress("Downloading FFmpeg", pct, read, total, label));
                }
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress("Extracting", 100, 0, null, null));

            var extractDir = Path.Combine(LocalDir, "extract-tmp");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var ffmpegSrc = Directory
                .EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            var ffprobeSrc = Directory
                .EnumerateFiles(extractDir, "ffprobe.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (ffmpegSrc is null || ffprobeSrc is null)
                throw new InvalidOperationException("FFmpeg binaries not found in archive.");

            Directory.CreateDirectory(LocalBinDir);
            File.Copy(ffmpegSrc, LocalFfmpeg, true);
            File.Copy(ffprobeSrc, LocalFfprobe, true);

            try { Directory.Delete(extractDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }

            FfmpegPath = LocalFfmpeg;
            FfprobePath = LocalFfprobe;
        }
    }
}
