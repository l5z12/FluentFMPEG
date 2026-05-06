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
using Sdcb.FFmpeg.Raw;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FluentFMPEG
{
    public sealed partial class MainWindow : Window
    {
        private MediaProfile? _primary;
        private MediaProfile? _secondary;
        private CancellationTokenSource? _cts;

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
            // Bundled libav* DLLs ship with the app — just verify they load.
            var ok = await FfmpegProvider.InitializeLibsAsync();
            if (!ok)
            {
                Log("[error] Failed to load FFmpeg native libraries.");
                return;
            }
            Log($"FFmpeg {FfmpegProvider.LibVersion}");

            var hwEncoders = new List<string>();
            foreach (var name in new[] { "h264_nvenc", "hevc_nvenc", "h264_amf", "hevc_amf", "h264_qsv", "hevc_qsv" })
            {
                if (Sdcb.FFmpeg.Codecs.Codec.FindEncoderByName(name) != null)
                    hwEncoders.Add(name);
            }
            Log(hwEncoders.Count > 0
                ? "Hardware encoders available: " + string.Join(", ", hwEncoders)
                : "No hardware encoders found — using CPU only.");

            PopulateHardwareCombo();
            RefreshConvertButton();
        }

        private void PopulateHardwareCombo()
        {
            HardwareEncoderCombo.Items.Clear();
            HardwareEncoderCombo.Items.Add(new ComboBoxItem { Content = "Auto", Tag = "auto" });
            if (OutputPresets.HasEncoder("h264_nvenc") || OutputPresets.HasEncoder("hevc_nvenc"))
                HardwareEncoderCombo.Items.Add(new ComboBoxItem { Content = "NVIDIA (NVENC)", Tag = "nvenc" });
            if (OutputPresets.HasEncoder("h264_amf") || OutputPresets.HasEncoder("hevc_amf"))
                HardwareEncoderCombo.Items.Add(new ComboBoxItem { Content = "AMD (AMF)", Tag = "amf" });
            if (OutputPresets.HasEncoder("h264_qsv") || OutputPresets.HasEncoder("hevc_qsv"))
                HardwareEncoderCombo.Items.Add(new ComboBoxItem { Content = "Intel (QSV)", Tag = "qsv" });
            HardwareEncoderCombo.Items.Add(new ComboBoxItem { Content = "CPU (software)", Tag = "cpu" });
            HardwareEncoderCombo.SelectedIndex = 0;
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
                FfmpegProvider.AreLibsReady
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
            var plan = preset.BuildPlan(mode, quality, adv);

            var progress = new Progress<EngineProgress>(p =>
            {
                if (Progress.IsIndeterminate) Progress.IsIndeterminate = false;
                Progress.Value = p.Percent;
                ProgressText.Text = $"{p.Percent:0.0}%";
            });

            _cts = new CancellationTokenSource();
            try
            {
                Log($"Encoding via libav* → {output}");
                var exit = await ConvertEngine.RunAsync(
                    _primary.InputPath, _secondary?.InputPath, output,
                    plan, adv, progress, Log, _cts.Token);
                Progress.IsIndeterminate = false;
                if (exit == ConvertEngine.CanceledExitCode)
                {
                    Progress.Value = 0;
                    ProgressText.Text = "Canceled";
                }
                else if (exit == 0)
                {
                    Progress.Value = 100;
                    ProgressText.Text = "Done";
                    Log("Saved → " + output);
                }
                else
                {
                    ProgressText.Text = $"Failed ({exit})";
                }
            }
            catch (OperationCanceledException)
            {
                // Defensive: shouldn't fire now that the engine returns CanceledExitCode,
                // but kept in case some Sdcb.FFmpeg internal raises one.
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

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            CancelButton.IsEnabled = false;
            ProgressText.Text = "Canceling…";
            Log("[cancel] Requested — finishing current frame…");
            _cts.Cancel();
        }

        private async void OnAboutClick(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }

        private AdvancedSettings GetAdvancedSettings()
        {
            string hwTag = "auto";
            if (HardwareEncoderCombo.SelectedItem is ComboBoxItem hwItem
                && hwItem.Tag is string ht)
                hwTag = ht;

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
                HardwareEncoder = hwTag,
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
        public static Task<MediaProfile> ProbeAsync(string path) =>
            Task.Run(() => Probe(path));

        private static MediaProfile Probe(string path)
        {
            using var fc = Sdcb.FFmpeg.Formats.FormatContext.OpenInputUrl(path);
            fc.LoadStreamInfo();

            // AVFormatContext::duration is in AV_TIME_BASE units (1µs).
            double? duration = null;
            if (fc.Duration > 0)
                duration = fc.Duration / (double)Sdcb.FFmpeg.Raw.ffmpeg.AV_TIME_BASE;

            var kind = MediaKind.Unknown;
            string vCodec = "", aCodec = "";
            int width = 0, height = 0;
            long nbFrames = 0;
            bool hasVideo = false, hasAudio = false;

            foreach (var stream in fc.Streams)
            {
                var par = stream.Codecpar;
                if (par == null) continue;
                var type = par.CodecType;
                if (type == Sdcb.FFmpeg.Raw.AVMediaType.Video)
                {
                    hasVideo = true;
                    vCodec = Sdcb.FFmpeg.Raw.ffmpeg.avcodec_get_name(par.CodecId) ?? "";
                    width = par.Width;
                    height = par.Height;
                    if (stream.NbFrames > nbFrames) nbFrames = stream.NbFrames;
                }
                else if (type == Sdcb.FFmpeg.Raw.AVMediaType.Audio)
                {
                    hasAudio = true;
                    aCodec = Sdcb.FFmpeg.Raw.ffmpeg.avcodec_get_name(par.CodecId) ?? "";
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
        // "auto" / "nvenc" / "amf" / "qsv" / "cpu" — used by H264/HEVC presets.
        public string? HardwareEncoder { get; init; }

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
        // Builder for the structured plan that drives the in-process engine.
        public required Func<Mode, Quality, AdvancedSettings, OutputPlan> PlanBuilder { get; init; }

        public bool AllowedIn(Mode mode) => mode switch
        {
            Mode.Convert => true,
            Mode.ExtractAudio => Category == PresetCategory.Audio,
            Mode.ExtractVideo => Category == PresetCategory.Video,
            Mode.Mix => Category == PresetCategory.Video,
            _ => false,
        };

        public OutputPlan BuildPlan(Mode mode, Quality q, AdvancedSettings adv) =>
            PlanBuilder(mode, q, adv);
    }

    internal static class OutputPresets
    {
        private static int Crf(int low, int med, int high, Quality q) => q switch
        {
            Quality.Low => low,
            Quality.High => high,
            _ => med,
        };

        private static long Br(int low, int med, int high, Quality q) => (q switch
        {
            Quality.Low => low,
            Quality.High => high,
            _ => med,
        }) * 1000L;

        // Audio settings the user dialed in (sample rate / channels / bitrate
        // override) are applied by the engine itself via AdvancedSettings, not
        // baked into the preset.
        private static long ResolveAudioBitrate(AdvancedSettings adv, long preset) =>
            adv.NormalizedAudioBitrate() is { Length: > 0 } b && long.TryParse(
                b.TrimEnd('k', 'K'), out var kbps) ? kbps * 1000L : preset;

        public static bool HasEncoder(string name) =>
            Sdcb.FFmpeg.Codecs.Codec.FindEncoderByName(name) != null;

        // Honors AdvancedSettings.HardwareEncoder ("nvenc"/"amf"/"qsv"/"cpu"/"auto").
        // For "auto" or unrecognized: NVENC → AMF → QSV → libx264. Explicit
        // selections fall back to CPU only if the requested vendor isn't compiled in.
        private static VideoSpec H264VideoSpec(Quality q, AdvancedSettings adv)
        {
            int cq = Crf(28, 22, 18, q);
            string pref = adv.HardwareEncoder ?? "auto";

            VideoSpec? Try(string vendor) => vendor switch
            {
                "nvenc" when HasEncoder("h264_nvenc") => NvencH264(cq),
                "amf"   when HasEncoder("h264_amf")   => AmfH264(cq),
                "qsv"   when HasEncoder("h264_qsv")   => QsvH264(cq),
                "cpu"                                  => SoftwareH264(cq),
                _ => null,
            };

            if (pref != "auto") return Try(pref) ?? AutoH264(cq);
            return AutoH264(cq);
        }

        private static VideoSpec HevcVideoSpec(Quality q, AdvancedSettings adv)
        {
            int cq = Crf(30, 24, 20, q);
            string pref = adv.HardwareEncoder ?? "auto";

            VideoSpec? Try(string vendor) => vendor switch
            {
                "nvenc" when HasEncoder("hevc_nvenc") => NvencHevc(cq),
                "amf"   when HasEncoder("hevc_amf")   => AmfHevc(cq),
                "qsv"   when HasEncoder("hevc_qsv")   => QsvHevc(cq),
                "cpu"                                  => SoftwareHevc(cq),
                _ => null,
            };

            if (pref != "auto") return Try(pref) ?? AutoHevc(cq);
            return AutoHevc(cq);
        }

        private static VideoSpec AutoH264(int cq)
        {
            if (HasEncoder("h264_nvenc")) return NvencH264(cq);
            if (HasEncoder("h264_amf"))   return AmfH264(cq);
            if (HasEncoder("h264_qsv"))   return QsvH264(cq);
            return SoftwareH264(cq);
        }

        private static VideoSpec AutoHevc(int cq)
        {
            if (HasEncoder("hevc_nvenc")) return NvencHevc(cq);
            if (HasEncoder("hevc_amf"))   return AmfHevc(cq);
            if (HasEncoder("hevc_qsv"))   return QsvHevc(cq);
            return SoftwareHevc(cq);
        }

        private static VideoSpec NvencH264(int cq) => new()
        {
            EncoderName = "h264_nvenc",
            PreferredPixelFormat = AVPixelFormat.Yuv420p,
            PrivateOptions = new()
            {
                ["preset"] = "p4", ["tune"] = "hq", ["rc"] = "vbr",
                ["cq"] = cq.ToString(CultureInfo.InvariantCulture), ["b:v"] = "0",
            },
        };

        private static VideoSpec NvencHevc(int cq) => new()
        {
            EncoderName = "hevc_nvenc",
            PreferredPixelFormat = AVPixelFormat.Yuv420p,
            PrivateOptions = new()
            {
                ["preset"] = "p4", ["tune"] = "hq", ["rc"] = "vbr",
                ["cq"] = cq.ToString(CultureInfo.InvariantCulture), ["b:v"] = "0",
            },
        };

        private static VideoSpec AmfH264(int cq) => new()
        {
            EncoderName = "h264_amf",
            PreferredPixelFormat = AVPixelFormat.Nv12,
            PrivateOptions = new()
            {
                ["quality"] = "balanced", ["rc"] = "cqp",
                ["qp_i"] = cq.ToString(CultureInfo.InvariantCulture),
                ["qp_p"] = cq.ToString(CultureInfo.InvariantCulture),
            },
        };

        private static VideoSpec AmfHevc(int cq) => new()
        {
            EncoderName = "hevc_amf",
            PreferredPixelFormat = AVPixelFormat.Nv12,
            PrivateOptions = new()
            {
                ["quality"] = "balanced", ["rc"] = "cqp",
                ["qp_i"] = cq.ToString(CultureInfo.InvariantCulture),
                ["qp_p"] = cq.ToString(CultureInfo.InvariantCulture),
            },
        };

        private static VideoSpec QsvH264(int cq) => new()
        {
            EncoderName = "h264_qsv",
            PreferredPixelFormat = AVPixelFormat.Nv12,
            PrivateOptions = new() { ["preset"] = "medium", ["global_quality"] = cq.ToString(CultureInfo.InvariantCulture) },
        };

        private static VideoSpec QsvHevc(int cq) => new()
        {
            EncoderName = "hevc_qsv",
            PreferredPixelFormat = AVPixelFormat.Nv12,
            PrivateOptions = new() { ["preset"] = "medium", ["global_quality"] = cq.ToString(CultureInfo.InvariantCulture) },
        };

        private static VideoSpec SoftwareH264(int cq) => new()
        {
            EncoderName = "libx264",
            PrivateOptions = new() { ["preset"] = "medium", ["crf"] = cq.ToString(CultureInfo.InvariantCulture) },
        };

        private static VideoSpec SoftwareHevc(int cq) => new()
        {
            EncoderName = "libx265",
            PrivateOptions = new() { ["preset"] = "medium", ["crf"] = cq.ToString(CultureInfo.InvariantCulture) },
        };

        public static readonly List<OutputPreset> All = new()
        {
            new()
            {
                Label = "MP4 (H.264 + AAC)", Extension = "mp4", Category = PresetCategory.Video,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "mp4",
                    Category = PresetCategory.Video,
                    VideoCopy = mode == Mode.Mix,
                    Video = mode == Mode.Mix ? null : H264VideoSpec(q, adv),
                    Audio = mode == Mode.ExtractVideo ? null : new AudioSpec
                    {
                        EncoderName = "aac",
                        Bitrate = ResolveAudioBitrate(adv, Br(128, 192, 256, q)),
                    },
                    MuxerOptions = new() { ["movflags"] = "+faststart" },
                },
            },
            new()
            {
                Label = "MKV (H.265 + AAC)", Extension = "mkv", Category = PresetCategory.Video,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "matroska",
                    Category = PresetCategory.Video,
                    VideoCopy = mode == Mode.Mix,
                    Video = mode == Mode.Mix ? null : HevcVideoSpec(q, adv),
                    Audio = mode == Mode.ExtractVideo ? null : new AudioSpec
                    {
                        EncoderName = "aac",
                        Bitrate = ResolveAudioBitrate(adv, Br(128, 192, 256, q)),
                    },
                },
            },
            new()
            {
                Label = "WebM (VP9 + Opus)", Extension = "webm", Category = PresetCategory.Video,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "webm",
                    Category = PresetCategory.Video,
                    VideoCopy = mode == Mode.Mix,
                    Video = mode == Mode.Mix ? null : new VideoSpec
                    {
                        EncoderName = "libvpx-vp9",
                        // -b:v 0 + -crf X = constant-quality mode for VP9.
                        // cpu-used / row-mt make a huge speed difference; libvpx-vp9
                        // defaults are unusably slow.
                        Bitrate = 0,
                        PrivateOptions = new()
                        {
                            ["crf"] = Crf(36, 30, 24, q).ToString(CultureInfo.InvariantCulture),
                            ["b:v"] = "0",
                            ["cpu-used"] = "4",
                            ["row-mt"] = "1",
                            ["deadline"] = "good",
                        },
                    },
                    Audio = mode == Mode.ExtractVideo ? null : new AudioSpec
                    {
                        EncoderName = "libopus",
                        Bitrate = ResolveAudioBitrate(adv, Br(96, 128, 192, q)),
                    },
                },
            },
            new()
            {
                Label = "Animated GIF", Extension = "gif", Category = PresetCategory.Video,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "gif",
                    Category = PresetCategory.Video,
                    Video = new VideoSpec
                    {
                        EncoderName = "gif",
                        PreferredPixelFormat = AVPixelFormat.Pal8,
                    },
                    // No audio in GIF.
                },
            },

            new()
            {
                Label = "MP3 (libmp3lame)", Extension = "mp3", Category = PresetCategory.Audio,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "mp3",
                    Category = PresetCategory.Audio,
                    Audio = new AudioSpec
                    {
                        EncoderName = "libmp3lame",
                        Bitrate = ResolveAudioBitrate(adv, Br(128, 192, 320, q)),
                    },
                },
            },
            new()
            {
                Label = "M4A (AAC)", Extension = "m4a", Category = PresetCategory.Audio,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "ipod",
                    Category = PresetCategory.Audio,
                    Audio = new AudioSpec
                    {
                        EncoderName = "aac",
                        Bitrate = ResolveAudioBitrate(adv, Br(128, 192, 256, q)),
                    },
                },
            },
            new()
            {
                Label = "Opus", Extension = "opus", Category = PresetCategory.Audio,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "ogg",
                    Category = PresetCategory.Audio,
                    Audio = new AudioSpec
                    {
                        EncoderName = "libopus",
                        Bitrate = ResolveAudioBitrate(adv, Br(96, 128, 192, q)),
                    },
                },
            },
            new()
            {
                Label = "FLAC (lossless)", Extension = "flac", Category = PresetCategory.Audio,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "flac",
                    Category = PresetCategory.Audio,
                    Audio = new AudioSpec { EncoderName = "flac" },
                },
            },
            new()
            {
                Label = "WAV (PCM)", Extension = "wav", Category = PresetCategory.Audio,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "wav",
                    Category = PresetCategory.Audio,
                    Audio = new AudioSpec { EncoderName = "pcm_s16le" },
                },
            },

            new()
            {
                Label = "PNG image", Extension = "png", Category = PresetCategory.Image,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "image2",
                    Category = PresetCategory.Image,
                    SingleFrame = true,
                    Video = new VideoSpec
                    {
                        EncoderName = "png",
                        PreferredPixelFormat = AVPixelFormat.Rgba,
                    },
                },
            },
            new()
            {
                Label = "JPEG image", Extension = "jpg", Category = PresetCategory.Image,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "image2",
                    Category = PresetCategory.Image,
                    SingleFrame = true,
                    Video = new VideoSpec
                    {
                        EncoderName = "mjpeg",
                        PreferredPixelFormat = AVPixelFormat.Yuvj420p,
                        GlobalQuality = Crf(8, 4, 2, q),
                    },
                },
            },
            new()
            {
                Label = "WebP image", Extension = "webp", Category = PresetCategory.Image,
                PlanBuilder = (mode, q, adv) => new OutputPlan
                {
                    Mode = mode,
                    MuxerFormat = "image2",
                    Category = PresetCategory.Image,
                    SingleFrame = true,
                    Video = new VideoSpec
                    {
                        EncoderName = "libwebp",
                        PreferredPixelFormat = AVPixelFormat.Yuva420p,
                        PrivateOptions = new() { ["quality"] = Crf(60, 80, 95, q).ToString(CultureInfo.InvariantCulture) },
                    },
                },
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
