using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FluentFMPEG
{
    public sealed partial class MainWindow : Window
    {
        private static readonly Regex TimeRegex =
            new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        private MediaProfile? _primary;
        private MediaProfile? _secondary;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _downloadCts;

        private bool _userEditedOutput;
        private bool _settingOutputFromCode;

        // Filtered view of presets matching the current mode.
        private readonly List<OutputPreset> _visiblePresets = new();

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindow.TitleBar.PreferredHeightOption =
                    Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            }

            ModeCombo.SelectedIndex = 0;
            QualityCombo.SelectedIndex = 1;

            OutputPathBox.TextChanged += (_, _) =>
            {
                if (_settingOutputFromCode) return;
                _userEditedOutput = !string.IsNullOrEmpty(OutputPathBox.Text);
            };

            _ = InitFfmpegAsync();
        }

        private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

        private Mode CurrentMode =>
            Enum.Parse<Mode>((string)((ComboBoxItem)ModeCombo.SelectedItem).Tag);

        private Quality CurrentQuality =>
            Enum.Parse<Quality>((string)((ComboBoxItem)QualityCombo.SelectedItem).Tag);

        private void Log(string line)
        {
            if (LogText.DispatcherQueue.HasThreadAccess)
                LogText.Text += line + Environment.NewLine;
            else
                LogText.DispatcherQueue.TryEnqueue(() => LogText.Text += line + Environment.NewLine);
        }

        // ---------- FFmpeg provisioning ----------

        private async Task InitFfmpegAsync()
        {
            ShowFfmpegBar(InfoBarSeverity.Informational, "Checking FFmpeg…", "", actionText: null);
            var ok = await FfmpegProvider.TryDetectAsync();
            if (ok)
            {
                FfmpegBar.IsOpen = false;
                return;
            }
            ShowFfmpegBar(
                InfoBarSeverity.Warning,
                "FFmpeg required",
                $"Download a static build (~80 MB) from BtbN/FFmpeg-Builds: {FfmpegProvider.AssetName()}",
                actionText: "Download",
                onAction: OnDownloadFfmpegClick);
        }

        private void ShowFfmpegBar(
            InfoBarSeverity severity, string title, string message,
            string? actionText, RoutedEventHandler? onAction = null)
        {
            FfmpegBar.Severity = severity;
            FfmpegBar.Title = title;
            FfmpegBar.Message = message;
            if (actionText is null)
            {
                FfmpegBar.ActionButton = null;
            }
            else
            {
                var btn = new Button { Content = actionText };
                if (onAction != null) btn.Click += onAction;
                FfmpegBar.ActionButton = btn;
            }
            FfmpegBar.IsOpen = true;
        }

        private async void OnDownloadFfmpegClick(object sender, RoutedEventArgs e)
        {
            _downloadCts = new CancellationTokenSource();
            var cancelBtn = new Button { Content = "Cancel" };
            cancelBtn.Click += (_, _) => _downloadCts?.Cancel();
            FfmpegBar.ActionButton = cancelBtn;
            FfmpegBar.Severity = InfoBarSeverity.Informational;
            FfmpegBar.Title = "Downloading FFmpeg";
            Progress.IsIndeterminate = true;
            Progress.Value = 0;
            ProgressText.Text = "Starting…";

            var progress = new Progress<FfmpegProvider.DownloadProgress>(p =>
            {
                var via = p.Mirror is null ? "" : $" via {p.Mirror}";
                FfmpegBar.Title = p.Status + via;
                if (p.TotalBytes is > 0)
                {
                    var mb = p.BytesRead / 1024.0 / 1024.0;
                    var totalMb = p.TotalBytes.Value / 1024.0 / 1024.0;
                    FfmpegBar.Message = $"{mb:0.0} / {totalMb:0.0} MB";
                    if (Progress.IsIndeterminate) Progress.IsIndeterminate = false;
                    Progress.Value = p.Percent;
                    ProgressText.Text = $"{p.Percent:0.0}%";
                }
                else
                {
                    FfmpegBar.Message = p.Mirror is null ? "Probing mirrors…" : "Connecting…";
                }
            });

            try
            {
                await FfmpegProvider.DownloadAsync(progress, _downloadCts.Token);
                FfmpegBar.IsOpen = false;
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
                ProgressText.Text = "Ready";
                RefreshConvertButton();
            }
            catch (OperationCanceledException)
            {
                ShowFfmpegBar(InfoBarSeverity.Warning, "Download canceled",
                    "FFmpeg is still required.",
                    actionText: "Download", onAction: OnDownloadFfmpegClick);
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
                ProgressText.Text = "Canceled";
            }
            catch (Exception ex)
            {
                ShowFfmpegBar(InfoBarSeverity.Error, "Download failed", ex.Message,
                    actionText: "Retry", onAction: OnDownloadFfmpegClick);
                Progress.IsIndeterminate = false;
                ProgressText.Text = "Failed";
            }
            finally
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        // ---------- Mode / Quality / Inputs ----------

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FormatCombo is null) return;
            ApplyMode();
        }

        private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
        {
            // Quality only affects ffmpeg args at run time; nothing else to do.
        }

        private void ApplyMode()
        {
            var mode = CurrentMode;

            SecondaryInputBorder.Visibility =
                mode == Mode.Mix ? Visibility.Visible : Visibility.Collapsed;

            PrimaryLabel.Text = mode switch
            {
                Mode.Mix => "Video file",
                Mode.ExtractAudio or Mode.ExtractVideo => "Source video",
                _ => "Input file",
            };

            // Filter presets to those allowed in this mode.
            _visiblePresets.Clear();
            foreach (var p in OutputPresets.All.Where(p => p.AllowedIn(mode)))
                _visiblePresets.Add(p);

            FormatCombo.Items.Clear();
            foreach (var p in _visiblePresets)
                FormatCombo.Items.Add(p.Label);

            // Auto-pick a sensible preset for this mode and the detected media.
            var preferred = OutputPresets.Recommend(mode, _primary?.Kind ?? MediaKind.Unknown);
            var idx = _visiblePresets.IndexOf(preferred);
            FormatCombo.SelectedIndex = idx >= 0 ? idx : (_visiblePresets.Count > 0 ? 0 : -1);

            FormatCombo.IsEnabled = _visiblePresets.Count > 0 && _primary != null;
            SaveAsButton.IsEnabled = _primary != null;

            UpdateSuggestedOutput();
            RefreshConvertButton();
        }

        private async void OnBrowsePrimaryClick(object sender, RoutedEventArgs e)
        {
            var file = await PickFileAsync();
            if (file != null) await LoadPrimaryAsync(file.Path);
        }

        private async void OnBrowseSecondaryClick(object sender, RoutedEventArgs e)
        {
            var file = await PickFileAsync();
            if (file != null) await LoadSecondaryAsync(file.Path);
        }

        private async Task<StorageFile?> PickFileAsync()
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeFilter.Add("*");
            return await picker.PickSingleFileAsync();
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to load";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }

        private async void OnDropPrimary(object sender, DragEventArgs e)
        {
            var path = await GetDroppedPathAsync(e);
            if (path != null) await LoadPrimaryAsync(path);
        }

        private async void OnDropSecondary(object sender, DragEventArgs e)
        {
            var path = await GetDroppedPathAsync(e);
            if (path != null) await LoadSecondaryAsync(path);
        }

        private static async Task<string?> GetDroppedPathAsync(DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return null;
            var items = await e.DataView.GetStorageItemsAsync();
            return items.OfType<StorageFile>().FirstOrDefault()?.Path;
        }

        private async Task LoadPrimaryAsync(string path)
        {
            InputPathText.Text = path;
            DetectionText.Text = "";
            _userEditedOutput = false;

            if (!FfmpegProvider.IsReady)
            {
                DetectionText.Text = "Install FFmpeg to probe this file.";
                _primary = new MediaProfile(path, MediaKind.Unknown, null, "");
                ApplyMode();
                return;
            }

            DetectionText.Text = "Probing…";
            try
            {
                _primary = await MediaProbe.ProbeAsync(path);
                DetectionText.Text = _primary.DetectionSummary;
            }
            catch (Exception ex)
            {
                DetectionText.Text = "Probe failed: " + ex.Message;
                _primary = new MediaProfile(path, MediaKind.Unknown, null, "");
            }

            ApplyMode();
        }

        private async Task LoadSecondaryAsync(string path)
        {
            SecondaryPathText.Text = path;
            SecondaryDetectionText.Text = "";

            if (!FfmpegProvider.IsReady)
            {
                _secondary = new MediaProfile(path, MediaKind.Unknown, null, "");
                RefreshConvertButton();
                return;
            }

            SecondaryDetectionText.Text = "Probing…";
            try
            {
                _secondary = await MediaProbe.ProbeAsync(path);
                SecondaryDetectionText.Text = _secondary.DetectionSummary;
            }
            catch (Exception ex)
            {
                SecondaryDetectionText.Text = "Probe failed: " + ex.Message;
                _secondary = new MediaProfile(path, MediaKind.Unknown, null, "");
            }

            RefreshConvertButton();
        }

        // ---------- Output path / format ----------

        private void OnFormatChanged(object sender, SelectionChangedEventArgs e) => UpdateSuggestedOutput();

        private void UpdateSuggestedOutput()
        {
            if (_primary == null || FormatCombo.SelectedIndex < 0
                || FormatCombo.SelectedIndex >= _visiblePresets.Count) return;

            var preset = _visiblePresets[FormatCombo.SelectedIndex];

            string newPath;
            if (_userEditedOutput && !string.IsNullOrEmpty(OutputPathBox.Text))
            {
                // Preserve user's custom name/dir; just swap the extension.
                var current = OutputPathBox.Text;
                var dir = Path.GetDirectoryName(current) ?? "";
                var name = Path.GetFileNameWithoutExtension(current);
                newPath = Path.Combine(dir, $"{name}.{preset.Extension}");
            }
            else
            {
                var dir = Path.GetDirectoryName(_primary.InputPath) ?? "";
                var name = Path.GetFileNameWithoutExtension(_primary.InputPath);
                var suffix = CurrentMode switch
                {
                    Mode.ExtractAudio => " (audio)",
                    Mode.ExtractVideo => " (video)",
                    Mode.Mix => " (mixed)",
                    _ => " (converted)",
                };
                newPath = Path.Combine(dir, $"{name}{suffix}.{preset.Extension}");
                if (string.Equals(newPath, _primary.InputPath, StringComparison.OrdinalIgnoreCase))
                    newPath = Path.Combine(dir, $"{name}{suffix}-1.{preset.Extension}");
            }

            _settingOutputFromCode = true;
            OutputPathBox.Text = newPath;
            _settingOutputFromCode = false;
        }

        private async void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            if (FormatCombo.SelectedIndex < 0
                || FormatCombo.SelectedIndex >= _visiblePresets.Count) return;
            var preset = _visiblePresets[FormatCombo.SelectedIndex];
            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            picker.FileTypeChoices.Add(preset.Label, new List<string> { "." + preset.Extension });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(OutputPathBox.Text);
            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                _settingOutputFromCode = true;
                OutputPathBox.Text = file.Path;
                _settingOutputFromCode = false;
                _userEditedOutput = true;
            }
        }

        // ---------- Conversion ----------

        private void RefreshConvertButton()
        {
            ConvertButton.IsEnabled =
                FfmpegProvider.IsReady
                && _primary != null
                && FormatCombo.SelectedIndex >= 0
                && (CurrentMode != Mode.Mix || _secondary != null);
        }

        private async void OnConvertClick(object sender, RoutedEventArgs e)
        {
            if (_primary == null
                || FormatCombo.SelectedIndex < 0
                || FormatCombo.SelectedIndex >= _visiblePresets.Count)
                return;
            if (!FfmpegProvider.IsReady)
            {
                Log("FFmpeg is not installed. Use the banner above to download it.");
                return;
            }
            if (CurrentMode == Mode.Mix && _secondary == null)
            {
                Log("Mix mode requires both a video and an audio file.");
                return;
            }

            var preset = _visiblePresets[FormatCombo.SelectedIndex];
            var output = OutputPathBox.Text;
            if (string.IsNullOrWhiteSpace(output))
            {
                Log("Output path is empty.");
                return;
            }

            ConvertButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            ModeCombo.IsEnabled = false;
            QualityCombo.IsEnabled = false;
            FormatCombo.IsEnabled = false;
            SaveAsButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            Progress.IsIndeterminate = true;
            Progress.Value = 0;
            ProgressText.Text = "Starting…";
            LogText.Text = "";

            var mode = CurrentMode;
            var quality = CurrentQuality;
            var adv = GetAdvancedSettings();
            var args = preset.BuildArgs(mode, quality, _primary.InputPath, _secondary?.InputPath, output, adv);

            // Pick a duration to drive progress: shorter of the two for Mix (-shortest), else primary.
            double? duration = mode == Mode.Mix
                ? MinNullable(_primary.DurationSeconds, _secondary?.DurationSeconds)
                : _primary.DurationSeconds;

            _cts = new CancellationTokenSource();
            try
            {
                Log("ffmpeg " + args);
                var exit = await RunFfmpegAsync(args, duration, _cts.Token);
                Progress.IsIndeterminate = false;
                if (exit == 0)
                {
                    Progress.Value = 100;
                    ProgressText.Text = "Done";
                    Log("Saved → " + output);
                }
                else
                {
                    ProgressText.Text = "Failed (" + exit + ")";
                }
            }
            catch (OperationCanceledException)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
                ProgressText.Text = "Canceled";
            }
            catch (Exception ex)
            {
                Progress.IsIndeterminate = false;
                ProgressText.Text = "Error";
                Log("[error] " + ex.Message);
            }
            finally
            {
                BrowseButton.IsEnabled = true;
                ModeCombo.IsEnabled = true;
                QualityCombo.IsEnabled = true;
                FormatCombo.IsEnabled = true;
                SaveAsButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                RefreshConvertButton();
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static double? MinNullable(double? a, double? b)
        {
            if (a is null) return b;
            if (b is null) return a;
            return Math.Min(a.Value, b.Value);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private async void OnAboutClick(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }

        private AdvancedSettings GetAdvancedSettings()
        {
            return new AdvancedSettings
            {
                Resolution = ReadEditableCombo(ResolutionCombo, "Keep"),
                FrameRate = ReadEditableCombo(FpsCombo, "Keep"),
                SampleRate = ReadEditableCombo(SampleRateCombo, "Keep"),
                Channels = ((ComboBoxItem)ChannelsCombo.SelectedItem).Tag is string tag
                    && int.TryParse(tag, out var ch) && ch > 0 ? ch : null,
                AudioBitrate = ReadEditableCombo(AudioBitrateCombo, "Use Quality setting"),
                TrimStart = NullIfBlank(TrimStartBox.Text),
                TrimEnd = NullIfBlank(TrimEndBox.Text),
                ExtraArgs = NullIfBlank(ExtraArgsBox.Text),
            };
        }

        private static string? ReadEditableCombo(ComboBox combo, string keepLabel)
        {
            // Editable ComboBox: prefer the selected item's content if it's a real value,
            // otherwise the typed Text. Treat the "keep" sentinel and empty as null.
            string? value = null;
            if (combo.SelectedItem is ComboBoxItem item && item.Content is string s)
                value = s;
            if (string.IsNullOrWhiteSpace(value))
                value = combo.Text;
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (string.Equals(value, keepLabel, StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }

        private static string? NullIfBlank(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private Task<int> RunFfmpegAsync(string args, double? durationSec, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(FfmpegProvider.FfmpegPath ?? "ffmpeg", args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>();

            proc.ErrorDataReceived += (_, ev) =>
            {
                if (ev.Data == null) return;
                Log(ev.Data);
                if (durationSec is > 0)
                {
                    var m = TimeRegex.Match(ev.Data);
                    if (m.Success)
                    {
                        var h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        var min = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        var s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                        var t = h * 3600 + min * 60 + s;
                        var pct = Math.Clamp(t / durationSec.Value * 100.0, 0, 100);
                        Progress.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (Progress.IsIndeterminate) Progress.IsIndeterminate = false;
                            Progress.Value = pct;
                            ProgressText.Text = $"{pct:0.0}%";
                        });
                    }
                }
            };
            proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

            try { proc.Start(); }
            catch (System.ComponentModel.Win32Exception)
            {
                tcs.TrySetException(new InvalidOperationException(
                    "ffmpeg.exe not found. Use the FFmpeg banner to install it."));
                return tcs.Task;
            }

            proc.BeginErrorReadLine();
            _ = Task.Run(() => proc.StandardOutput.ReadToEnd());

            ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                tcs.TrySetCanceled();
            });

            return tcs.Task;
        }
    }

    // ---------- Probe ----------

    internal sealed record MediaProfile(
        string InputPath,
        MediaKind Kind,
        double? DurationSeconds,
        string DetectionSummary);

    internal enum MediaKind { Unknown, Video, Audio, Image, AnimatedImage }

    internal static class MediaProbe
    {
        public static async Task<MediaProfile> ProbeAsync(string path)
        {
            var psi = new ProcessStartInfo(FfmpegProvider.FfprobePath ?? "ffprobe",
                $"-v error -print_format json -show_format -show_streams \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string stdout, stderr;
            using (var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffprobe."))
            {
                stdout = await p.StandardOutput.ReadToEndAsync();
                stderr = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(stderr) ? "ffprobe failed." : stderr.Trim());
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            double? duration = null;
            if (root.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
                duration = ds;

            var kind = MediaKind.Unknown;
            string vCodec = "", aCodec = "";
            int width = 0, height = 0, nbFrames = 0;
            bool hasVideo = false, hasAudio = false;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.GetProperty("codec_type").GetString();
                    if (type == "video")
                    {
                        hasVideo = true;
                        vCodec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                        if (s.TryGetProperty("width", out var w)) width = w.GetInt32();
                        if (s.TryGetProperty("height", out var h)) height = h.GetInt32();
                        if (s.TryGetProperty("nb_frames", out var nf)
                            && int.TryParse(nf.GetString(), out var n)) nbFrames = n;
                    }
                    else if (type == "audio")
                    {
                        hasAudio = true;
                        aCodec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                    }
                }
            }

            if (hasVideo && (duration is > 1 || nbFrames > 1)) kind = MediaKind.Video;
            else if (hasVideo && IsAnimatedExt(path)) kind = MediaKind.AnimatedImage;
            else if (hasVideo) kind = MediaKind.Image;
            else if (hasAudio) kind = MediaKind.Audio;

            var summary = kind switch
            {
                MediaKind.Video => $"Video · {vCodec} {width}x{height}" +
                                   (hasAudio ? $" + {aCodec}" : "") +
                                   (duration is { } v ? $" · {FormatDuration(v)}" : ""),
                MediaKind.Audio => $"Audio · {aCodec}" +
                                   (duration is { } a ? $" · {FormatDuration(a)}" : ""),
                MediaKind.Image => $"Image · {vCodec} {width}x{height}",
                MediaKind.AnimatedImage => $"Animated image · {vCodec} {width}x{height}",
                _ => "Unknown media type",
            };

            return new MediaProfile(path, kind, duration, summary);
        }

        private static bool IsAnimatedExt(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".gif" or ".webp" or ".apng";
        }

        private static string FormatDuration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
    }

    // ---------- Mode / Quality / Presets ----------

    internal enum Mode { Convert, ExtractAudio, ExtractVideo, Mix }
    internal enum Quality { Low, Medium, High }
    internal enum PresetCategory { Video, Audio, Image }

    internal sealed class AdvancedSettings
    {
        public string? Resolution { get; init; }     // e.g. "1920x1080"
        public string? FrameRate { get; init; }      // e.g. "30"
        public string? SampleRate { get; init; }     // e.g. "48000"
        public int? Channels { get; init; }          // 1 / 2
        public string? AudioBitrate { get; init; }   // e.g. "192k" or "192"
        public string? TrimStart { get; init; }      // hh:mm:ss or seconds
        public string? TrimEnd { get; init; }
        public string? ExtraArgs { get; init; }

        public string NormalizedAudioBitrate()
        {
            if (string.IsNullOrEmpty(AudioBitrate)) return "";
            return AudioBitrate.EndsWith("k", StringComparison.OrdinalIgnoreCase)
                ? AudioBitrate
                : AudioBitrate + "k";
        }
    }

    internal sealed class OutputPreset
    {
        public required string Label { get; init; }
        public required string Extension { get; init; }
        public required PresetCategory Category { get; init; }
        public required Func<Quality, string> VideoArgs { get; init; }
        public required Func<Quality, string> AudioArgs { get; init; }

        public bool AllowedIn(Mode mode) => mode switch
        {
            Mode.Convert => true,
            Mode.ExtractAudio => Category == PresetCategory.Audio,
            Mode.ExtractVideo => Category == PresetCategory.Video,
            Mode.Mix => Category == PresetCategory.Video,
            _ => false,
        };

        public string BuildArgs(
            Mode mode, Quality q, string in1, string? in2, string output, AdvancedSettings adv)
        {
            var v = VideoArgs(q);
            var a = AudioArgs(q);
            var outQ = $"\"{output}\"";

            // Trim: input seek is fast but inaccurate; output seek (after -i) matches the
            // user's absolute-timestamp expectation. Use output seek for both.
            var trimOut = "";
            if (!string.IsNullOrEmpty(adv.TrimStart)) trimOut += $"-ss {adv.TrimStart} ";
            if (!string.IsNullOrEmpty(adv.TrimEnd)) trimOut += $"-to {adv.TrimEnd} ";

            // Video filter chain. Skip if the preset already supplies its own -vf
            // (e.g. the GIF preset's palettegen pipeline) since merging filter graphs
            // safely is non-trivial.
            var vFilters = new List<string>();
            if (!string.IsNullOrEmpty(adv.Resolution)) vFilters.Add($"scale={adv.Resolution}");
            if (!string.IsNullOrEmpty(adv.FrameRate)) vFilters.Add($"fps={adv.FrameRate}");
            var vfArg = (vFilters.Count > 0 && !v.Contains("-vf"))
                ? $"-vf \"{string.Join(",", vFilters)}\" "
                : "";

            // Audio overrides — placed AFTER preset audio args so they win.
            var audioExtra = "";
            var bitrate = adv.NormalizedAudioBitrate();
            if (!string.IsNullOrEmpty(bitrate)) audioExtra += $"-b:a {bitrate} ";
            if (!string.IsNullOrEmpty(adv.SampleRate)) audioExtra += $"-ar {adv.SampleRate} ";
            if (adv.Channels is { } ch) audioExtra += $"-ac {ch} ";

            var extra = string.IsNullOrEmpty(adv.ExtraArgs) ? "" : adv.ExtraArgs + " ";

            return mode switch
            {
                Mode.ExtractAudio =>
                    $"-y -i \"{in1}\" {trimOut}-vn {a} {audioExtra}{extra}{outQ}",
                Mode.ExtractVideo =>
                    $"-y -i \"{in1}\" {trimOut}-an {vfArg}{v} {extra}{outQ}",
                Mode.Mix =>
                    // -c:v copy means user-specified video filters can't apply here.
                    $"-y -i \"{in1}\" -i \"{in2}\" {trimOut}-map 0:v:0 -map 1:a:0 -c:v copy {a} {audioExtra}-shortest {extra}{outQ}",
                _ => Category switch
                {
                    PresetCategory.Video =>
                        $"-y -i \"{in1}\" {trimOut}{vfArg}{v} {a} {audioExtra}{extra}{outQ}",
                    PresetCategory.Audio =>
                        $"-y -i \"{in1}\" {trimOut}-vn {a} {audioExtra}{extra}{outQ}",
                    PresetCategory.Image =>
                        $"-y -i \"{in1}\" -frames:v 1 {vfArg}{v} {extra}{outQ}",
                    _ => $"-y -i \"{in1}\" {outQ}",
                },
            };
        }
    }

    internal static class OutputPresets
    {
        private static string Crf(int low, int med, int high, Quality q) => q switch
        {
            Quality.Low => low.ToString(CultureInfo.InvariantCulture),
            Quality.High => high.ToString(CultureInfo.InvariantCulture),
            _ => med.ToString(CultureInfo.InvariantCulture),
        };

        private static string Br(int low, int med, int high, Quality q) => q switch
        {
            Quality.Low => $"{low}k",
            Quality.High => $"{high}k",
            _ => $"{med}k",
        };

        public static readonly List<OutputPreset> All = new()
        {
            // Video presets
            new()
            {
                Label = "MP4 (H.264 + AAC)", Extension = "mp4", Category = PresetCategory.Video,
                VideoArgs = q => $"-c:v libx264 -preset medium -crf {Crf(28, 22, 18, q)} -movflags +faststart",
                AudioArgs = q => $"-c:a aac -b:a {Br(128, 192, 256, q)}",
            },
            new()
            {
                Label = "MKV (H.265 + AAC)", Extension = "mkv", Category = PresetCategory.Video,
                VideoArgs = q => $"-c:v libx265 -preset medium -crf {Crf(30, 24, 20, q)}",
                AudioArgs = q => $"-c:a aac -b:a {Br(128, 192, 256, q)}",
            },
            new()
            {
                Label = "WebM (VP9 + Opus)", Extension = "webm", Category = PresetCategory.Video,
                VideoArgs = q => $"-c:v libvpx-vp9 -b:v 0 -crf {Crf(36, 30, 24, q)}",
                AudioArgs = q => $"-c:a libopus -b:a {Br(96, 128, 192, q)}",
            },
            new()
            {
                Label = "Animated GIF", Extension = "gif", Category = PresetCategory.Video,
                VideoArgs = q =>
                {
                    var fps = q switch { Quality.Low => 10, Quality.High => 24, _ => 15 };
                    var w = q switch { Quality.Low => 320, Quality.High => 720, _ => 480 };
                    return $"-vf \"fps={fps},scale={w}:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -an";
                },
                AudioArgs = q => "",
            },

            // Audio presets
            new()
            {
                Label = "MP3 (libmp3lame)", Extension = "mp3", Category = PresetCategory.Audio,
                VideoArgs = q => "",
                AudioArgs = q => $"-c:a libmp3lame -b:a {Br(128, 192, 320, q)}",
            },
            new()
            {
                Label = "M4A (AAC)", Extension = "m4a", Category = PresetCategory.Audio,
                VideoArgs = q => "",
                AudioArgs = q => $"-c:a aac -b:a {Br(128, 192, 256, q)}",
            },
            new()
            {
                Label = "Opus", Extension = "opus", Category = PresetCategory.Audio,
                VideoArgs = q => "",
                AudioArgs = q => $"-c:a libopus -b:a {Br(96, 128, 192, q)}",
            },
            new()
            {
                Label = "FLAC (lossless)", Extension = "flac", Category = PresetCategory.Audio,
                VideoArgs = q => "",
                AudioArgs = q => "-c:a flac",
            },
            new()
            {
                Label = "WAV (PCM)", Extension = "wav", Category = PresetCategory.Audio,
                VideoArgs = q => "",
                AudioArgs = q => "-c:a pcm_s16le",
            },

            // Image presets
            new()
            {
                Label = "PNG image", Extension = "png", Category = PresetCategory.Image,
                VideoArgs = q => "",
                AudioArgs = q => "",
            },
            new()
            {
                Label = "JPEG image", Extension = "jpg", Category = PresetCategory.Image,
                VideoArgs = q => $"-q:v {Crf(8, 4, 2, q)}",
                AudioArgs = q => "",
            },
            new()
            {
                Label = "WebP image", Extension = "webp", Category = PresetCategory.Image,
                VideoArgs = q => $"-q:v {Crf(60, 80, 95, q)}",
                AudioArgs = q => "",
            },
        };

        public static OutputPreset Recommend(Mode mode, MediaKind kind)
        {
            return (mode, kind) switch
            {
                (Mode.ExtractAudio, _) => Find("mp3"),
                (Mode.ExtractVideo, _) => Find("mp4"),
                (Mode.Mix, _) => Find("mp4"),
                (_, MediaKind.Video) => Find("mp4"),
                (_, MediaKind.Audio) => Find("mp3"),
                (_, MediaKind.AnimatedImage) => Find("gif"),
                (_, MediaKind.Image) => Find("png"),
                _ => All[0],
            };
        }

        private static OutputPreset Find(string ext) =>
            All.First(p => p.Extension == ext);
    }
}
