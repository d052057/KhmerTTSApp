using EdgeTtsSharp;
using Microsoft.Win32;
using NWaves.Audio;
using NWaves.Features;
using NWaves.Signals;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Xabe.FFmpeg;

namespace KhmerTTSApp
{
    public partial class MainWindow : Window
    {
        private string _loadedFileExtension = ".srt";
        private const string IgnoreFileName = "ignore_words.txt";

        // GLOBALIZED: Single source of truth for your FFmpeg path
        private string _resolvedFfmpegPath = string.Empty;
        public class SubtitleBlock
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string Text { get; set; } = string.Empty;
            public double TargetDuration => (End - Start).TotalSeconds;
        }

        public MainWindow()
        {
            InitializeComponent();
            FFmpeg.SetExecutablesPath(AppDomain.CurrentDomain.BaseDirectory);
            EnsureIgnoreFileExists();
            InitializeGlobalFfmpegPath();
        }

        private void InitializeGlobalFfmpegPath()
        {
            string executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            string localAppPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, executableName);
            _resolvedFfmpegPath = File.Exists(localAppPath) ? localAppPath : executableName;
        }
        private void EnsureIgnoreFileExists()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IgnoreFileName);
                if (!File.Exists(configPath))
                {
                    string defaultContent = "[music]\r\n(music)\r\n[applause]\r\n(applause)\r\n[noise]\r\n(noise)\r\n[laughter]";
                    File.WriteAllText(configPath, defaultContent, Encoding.UTF8);
                }
            }
            catch
            {
                // Fail-safe block to avoid app initialization freezes
            }
        }

        private List<string> LoadIgnoreWords()
        {
            var ignoreList = new List<string>();
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IgnoreFileName);
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath, Encoding.UTF8);
                    foreach (var line in lines)
                    {
                        string clean = line.Trim();
                        if (!string.IsNullOrEmpty(clean))
                        {
                            ignoreList.Add(clean);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load ignore words: {ex.Message}");
            }
            return ignoreList;
        }
        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Subtitle Files (*.srt;*.vtt)|*.srt;*.vtt|All Files (*.*)|*.*";
            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    string fileContent = File.ReadAllText(openFileDialog.FileName, Encoding.UTF8);
                    TxtInput.Text = fileContent;
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(openFileDialog.FileName);
                    _loadedFileExtension = Path.GetExtension(openFileDialog.FileName).ToLower();
                    TxtFileName.Text = fileNameWithoutExtension + "_Voice";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not read selected file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnAnalyzeVideo_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Video Files (*.mp4;*.mkv;*.avi)|*.mp4;*.mkv;*.avi|All Files (*.*)|*.*";
            openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            if (openFileDialog.ShowDialog() == true)
            {
                BtnAnalyzeVideo.IsEnabled = false;
                BtnAnalyzeVideo.Content = "Analyzing...";
                Mouse.OverrideCursor = Cursors.Wait;

                try
                {
                    await AnalyzeOriginalVoiceFromMp4(openFileDialog.FileName);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    BtnAnalyzeVideo.IsEnabled = true;
                    BtnAnalyzeVideo.Content = "Analyze MP4";
                }
            }
        }

        private async Task AnalyzeOriginalVoiceFromMp4(string mp4Path)
        {
            string tempWavPath = Path.Combine(Path.GetTempPath(), "extracted_temp.wav");
            try
            {
                string args = new StringBuilder().AppendFormat("-y -i \"{0}\" -vn -acodec pcm_s16le -ac 1 -ar 16000 \"{1}\"", mp4Path, tempWavPath).ToString();
                var ffmpegTask = RunFFmpegAsync(args);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45));

                var completedTask = await Task.WhenAny(ffmpegTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("FFmpeg operation timed out. Ensure the MP4 video is not corrupted.");
                }

                if (!File.Exists(tempWavPath))
                {
                    throw new FileNotFoundException("FFmpeg failed to extract audio from the video file.");
                }

                using (var stream = File.OpenRead(tempWavPath))
                {
                    var waveFile = new WaveFile(stream);
                    DiscreteSignal signal = waveFile[Channels.Left];

                    float[] samples = signal.Samples;
                    int sampleRate = signal.SamplingRate;
                    int maxSamplesToProcess = Math.Min(samples.Length, sampleRate * 30);

                    var detectedPitches = new System.Collections.Generic.List<float>();
                    int windowSize = (int)(sampleRate * 0.20);
                    int hopSize = (int)(sampleRate * 0.10);

                    for (int pos = 0; pos + windowSize < maxSamplesToProcess; pos += hopSize)
                    {
                        float framePitch = Pitch.FromYin(samples, sampleRate, pos, pos + windowSize, 50, 400);
                        if (framePitch > 60 && framePitch < 350)
                        {
                            detectedPitches.Add(framePitch);
                        }
                    }

                    if (detectedPitches.Count > 0)
                    {
                        double averagePitchHz = detectedPitches.Average();
                        if (averagePitchHz < 165)
                        {
                            CboVoice.SelectedIndex = 0;
                            double pitchOffsetPercent = ((averagePitchHz - 120) / 120) * 100;
                            SldRate.Value = 0;
                            SldPitch.Value = Math.Clamp((int)pitchOffsetPercent, -25, 25);
                        }
                        else
                        {
                            CboVoice.SelectedIndex = 1;
                            double pitchOffsetPercent = ((averagePitchHz - 210) / 210) * 100;
                            SldRate.Value = 0;
                            SldPitch.Value = Math.Clamp((int)pitchOffsetPercent, -25, 25);
                        }

                        SldRate.IsEnabled = false;
                        SldPitch.IsEnabled = false;
                    }
                    else
                    {
                        throw new Exception("No clear human vocal tones were detected in the video sample.");
                    }
                }

                MessageBox.Show("Voice analysis completed! Matched gender voice profiles and assigned optimal slider settings.", "Analysis Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not automatically analyze voice metrics:\n{ex.Message}", "Analysis Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (File.Exists(tempWavPath))
                {
                    try { File.Delete(tempWavPath); } catch { }
                }
            }
        }
        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string rawText = TxtInput.Text.Trim();
            string outputName = TxtFileName.Text.Trim();

            if (string.IsNullOrWhiteSpace(rawText))
            {
                string warningMessage = "Please open a file or enter some Khmer text first.";
                if (MessageBox.Show(warningMessage, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning) == MessageBoxResult.OK)
                {
                    Clipboard.SetText(warningMessage);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(outputName))
            {
                MessageBox.Show("Please type a valid filename.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                outputName = outputName.Replace(c.ToString(), "");
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "MP3 Audio File (*.mp3)|*.mp3";
            saveFileDialog.FileName = outputName;
            saveFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            if (saveFileDialog.ShowDialog() != true) return;

            string targetAudioPath = saveFileDialog.FileName;
            string targetSubtitlePath = Path.ChangeExtension(targetAudioPath, _loadedFileExtension);

            BtnGenerate.IsEnabled = false;
            BtnOpenFile.IsEnabled = false;
            BtnGenerate.Content = "Processing...";
            Mouse.OverrideCursor = Cursors.Wait;

            string tempDir = Path.Combine(Path.GetTempPath(), "KhmerTTS_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                List<string> wordsToIgnore = LoadIgnoreWords();
                string[] lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                List<SubtitleBlock> subBlocks = new List<SubtitleBlock>();
                TimeSpan currentStart = TimeSpan.Zero;
                TimeSpan currentEnd = TimeSpan.Zero;
                bool readingText = false;
                StringBuilder textAccumulator = new StringBuilder();
                string lastAddedText = string.Empty;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (trimmedLine.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Regex.IsMatch(trimmedLine, @"^\d+$")) continue;

                    if (trimmedLine.Contains("-->"))
                    {
                        if (readingText && textAccumulator.Length > 0)
                        {
                            subBlocks.Add(new SubtitleBlock { Start = currentStart, End = currentEnd, Text = textAccumulator.ToString().Trim() });
                            textAccumulator.Clear();
                        }

                        var parts = Regex.Split(trimmedLine, @"\s*-->\s*");
                        if (parts.Length >= 2)
                        {
                            // Target the array index fields directly before calling Trim and Replace
                            string startStr = parts[0].Trim().Replace(',', '.');
                            string endStr = parts[1].Trim().Replace(',', '.');


                            if (startStr.Count(c => c == ':') == 1) startStr = "00:" + startStr;
                            if (endStr.Count(c => c == ':') == 1) endStr = "00:" + endStr;

                            TimeSpan.TryParse(startStr, System.Globalization.CultureInfo.InvariantCulture, out currentStart);
                            TimeSpan.TryParse(endStr, System.Globalization.CultureInfo.InvariantCulture, out currentEnd);
                            readingText = true;
                        }
                        continue;
                    }

                    if (readingText && !string.IsNullOrWhiteSpace(trimmedLine))
                    {
                        foreach (string customWord in wordsToIgnore)
                        {
                            trimmedLine = Regex.Replace(trimmedLine, Regex.Escape(customWord), "", RegexOptions.IgnoreCase);
                        }

                        trimmedLine = trimmedLine.Replace("\"", "").Replace("'", "").Trim();

                        if (trimmedLine.Length > 0)
                        {
                            if (trimmedLine.Equals(lastAddedText, StringComparison.OrdinalIgnoreCase)) continue;
                            textAccumulator.Append(trimmedLine).Append(" ");
                            lastAddedText = trimmedLine;
                        }
                    }
                }

                if (readingText && textAccumulator.Length > 0)
                {
                    subBlocks.Add(new SubtitleBlock { Start = currentStart, End = currentEnd, Text = textAccumulator.ToString().Trim() });
                }

                if (subBlocks.Count == 0)
                {
                    throw new Exception("No valid timestamp blocks or text content found inside this file.");
                }
                string selectedVoiceName = CboVoice.SelectedIndex == 0 ? "km-KH-PisethNeural" : "km-KH-SreymomNeural";
                var voice = await EdgeTts.GetVoice(selectedVoiceName);

                List<string> processedPaths = new List<string>();

                for (int i = 0; i < subBlocks.Count; i++)
                {
                    string rawSeg = Path.Combine(tempDir, $"raw_{i}.mp3");
                    string fixedSeg = Path.Combine(tempDir, $"fixed_{i}.mp3");

                    await voice.SaveAudioToFile(subBlocks[i].Text, rawSeg);

                    var info = await FFmpeg.GetMediaInfo(rawSeg);
                    double duration = info.Duration.TotalSeconds;

                    double speedRatio = 1.0;
                    if (duration > subBlocks[i].TargetDuration && subBlocks[i].TargetDuration > 0)
                    {
                        speedRatio = duration / subBlocks[i].TargetDuration;
                    }

                    double sliderSpeedFactor = 1.0 + (SldRate.Value / 100.0);
                    double combinedSpeedRatio = speedRatio * sliderSpeedFactor;

                    if (combinedSpeedRatio > 2.0) combinedSpeedRatio = 2.0;
                    if (combinedSpeedRatio < 0.5) combinedSpeedRatio = 0.5;

                    double pitchFactor = 1.0 + (SldPitch.Value / 100.0);
                    int targetSampleRate = 24000;
                    int adjustedSampleRate = (int)(targetSampleRate * pitchFactor);

                    string args = new StringBuilder().AppendFormat("-y -i \"{0}\" -filter:a \"asetrate={1},aresample=44100,atempo={2:F2}\" \"{3}\"", rawSeg, adjustedSampleRate, combinedSpeedRatio, fixedSeg).ToString();

                    await RunFFmpegAsync(args);
                    processedPaths.Add(fixedSeg);
                }

                string manifestPath = Path.Combine(tempDir, "concat_list.txt");
                StringBuilder manifestBuilder = new StringBuilder();
                TimeSpan timelineCursor = TimeSpan.Zero;

                for (int i = 0; i < processedPaths.Count; i++)
                {
                    if (subBlocks[i].Start > timelineCursor)
                    {
                        double gapSeconds = (subBlocks[i].Start - timelineCursor).TotalSeconds;

                        if (gapSeconds > 0.05)
                        {
                            string silenceFile = Path.Combine(tempDir, $"silence_{i}.mp3");
                            string silenceArgs = new StringBuilder().AppendFormat("-y -f lavfi -i anullsrc=r=44100:c=stereo -t {0} -c:a libmp3lame \"{1}\"", gapSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), silenceFile).ToString();

                            await RunFFmpegAsync(silenceArgs);

                            if (File.Exists(silenceFile))
                            {
                                string formattedSilencePath = silenceFile.Replace(Path.DirectorySeparatorChar, '/');
                                manifestBuilder.AppendLine($"file '{formattedSilencePath}'");
                                timelineCursor += TimeSpan.FromSeconds(gapSeconds);
                            }
                        }
                        else
                        {
                            timelineCursor = subBlocks[i].Start;
                        }
                    }

                    string formattedAudioPath = processedPaths[i].Replace(Path.DirectorySeparatorChar, '/');
                    manifestBuilder.AppendLine($"file '{formattedAudioPath}'");

                    var info = await FFmpeg.GetMediaInfo(processedPaths[i]);
                    timelineCursor += info.Duration;
                }

                await File.WriteAllTextAsync(manifestPath, manifestBuilder.ToString(), new UTF8Encoding(false));
                string concatArgs = new StringBuilder().AppendFormat("-y -safe 0 -f concat -i \"{0}\" -c copy \"{1}\"", manifestPath, targetAudioPath).ToString();

                await RunFFmpegAsync(concatArgs);

                await File.WriteAllTextAsync(targetSubtitlePath, rawText, Encoding.UTF8);
                Mouse.OverrideCursor = null;
                MessageBox.Show("Files generated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                await ffmpegAudit(subBlocks, tempDir, processedPaths, manifestPath, targetAudioPath);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = new StringBuilder().AppendFormat("/select,\"{0}\"", targetAudioPath).ToString(),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show($"Could not export assets: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                Mouse.OverrideCursor = null;
                BtnGenerate.IsEnabled = true;
                BtnOpenFile.IsEnabled = true;
                BtnGenerate.Content = "Generate MP3";
            }
        }
        private async void BtnMergeVideo_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Please choose the original Video File (MP4) first, then select the new Khmer Audio track (MP3).", "Instructions", MessageBoxButton.OK, MessageBoxImage.Information);

            OpenFileDialog videoDialog = new OpenFileDialog { Filter = "Video Files (*.mp4)|*.mp4", Title = "Step 1: Select Original MP4 Video File" };
            if (videoDialog.ShowDialog() != true) return;

            OpenFileDialog audioDialog = new OpenFileDialog { Filter = "Audio Files (*.mp3)|*.mp3", Title = "Step 2: Select New Khmer MP3 Audio File" };
            if (audioDialog.ShowDialog() != true) return;

            SaveFileDialog outputDialog = new SaveFileDialog { Filter = "MP4 Video File (*.mp4)|*.mp4", Title = "Step 3: Save Final Merged Video Asset", FileName = Path.GetFileNameWithoutExtension(videoDialog.FileName) + "_KhmerVoiceover" };
            if (outputDialog.ShowDialog() != true) return;

            BtnMergeVideo.IsEnabled = false;
            BtnMergeVideo.Content = "Merging Assets...";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                string finalArguments = new StringBuilder().AppendFormat("-y -i \"{0}\" -i \"{1}\" -map 0:v:0 -map 0:a:0 -map 1:a:0 -c:v copy -c:a:0 copy -c:a:1 aac -metadata:s:a:0 title=\"Original\" -metadata:s:a:1 title=\"Khmer\" -shortest \"{2}\"", videoDialog.FileName, audioDialog.FileName, outputDialog.FileName).ToString();

                await RunFFmpegAsync(finalArguments);

                Mouse.OverrideCursor = null;
                MessageBox.Show("Khmer voice audio merged into the MP4 video asset successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = new StringBuilder().AppendFormat("/select,\"{0}\"", outputDialog.FileName).ToString(),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show($"Could not bind audio track to video frame maps: {ex.Message}", "Muxing Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                BtnMergeVideo.IsEnabled = true;
                BtnMergeVideo.Content = "Merge MP4 & MP3";
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtInput.Clear();
            TxtFileName.Text = "KhmerAudio";
            _loadedFileExtension = ".srt";

            SldRate.Value = 0;
            SldPitch.Value = 0;
            SldRate.IsEnabled = true;
            SldPitch.IsEnabled = true;

            CboVoice.SelectedIndex = 0;
        }

        private async Task RunFFmpegAsync(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _resolvedFfmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(psi))
            {
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
            }
        }

        private async Task ffmpegAudit(
            List<SubtitleBlock> subBlocks,
            string tempDir,
            List<string> processedPaths,
            string manifestPath,
            string targetAudioPath)
        {
            string manualBatchPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Run_FFmpeg_Manually.bat");
            StringBuilder batchBuilder = new StringBuilder();

            batchBuilder.AppendLine("@echo off");
            batchBuilder.AppendLine("echo ===================================================");
            batchBuilder.AppendLine("echo      RUNNING MANUALLY ENCODED FFMPEG AUDIO TASKS      ");
            batchBuilder.AppendLine("echo ===================================================");
            batchBuilder.AppendLine("echo.");

            batchBuilder.AppendLine("echo --- STAGE 1: RENDERING AND STRETCHING CLIPS ---");
            for (int i = 0; i < subBlocks.Count; i++)
            {
                string rawSeg = Path.Combine(tempDir, $"raw_{i}.mp3");
                string fixedSeg = Path.Combine(tempDir, $"fixed_{i}.mp3");

                double speedRatio = 1.0;
                try
                {
                    var info = await FFmpeg.GetMediaInfo(rawSeg);
                    if (info.Duration.TotalSeconds > subBlocks[i].TargetDuration && subBlocks[i].TargetDuration > 0)
                        speedRatio = info.Duration.TotalSeconds / subBlocks[i].TargetDuration;
                }
                catch { }

                double combinedSpeedRatio = speedRatio * (1.0 + (SldRate.Value / 100.0));
                if (combinedSpeedRatio > 2.0) combinedSpeedRatio = 2.0;
                if (combinedSpeedRatio < 0.5) combinedSpeedRatio = 0.5;

                int adjustedSampleRate = (int)(24000 * (1.0 + (SldPitch.Value / 100.0)));
                string segArgs = $"-y -i '{rawSeg}' -filter:a 'asetrate={adjustedSampleRate},aresample=44100,atempo={combinedSpeedRatio:F2}' '{fixedSeg}'";
                batchBuilder.AppendLine($" \"{_resolvedFfmpegPath}\" {segArgs} ");

            }

            batchBuilder.AppendLine("echo.");
            batchBuilder.AppendLine("echo --- STAGE 2: INJECTING SILENCE FILES AND ASSEMBLING MASTER MP3 ---");

            TimeSpan cursor = TimeSpan.Zero;
            for (int i = 0; i < processedPaths.Count; i++)
            {
                if (subBlocks[i].Start > cursor)
                {
                    double gapSeconds = (subBlocks[i].Start - cursor).TotalSeconds;
                    string silenceFile = Path.Combine(tempDir, $"silence_{i}.mp3");

                    string silenceArgs = $"-y -f lavfi -i anullsrc=r=44100:c=stereo -t {gapSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} -c:a libmp3lame '{silenceFile}'";
                    batchBuilder.AppendFormat(" \"{0}\" {1} ", _resolvedFfmpegPath, silenceArgs).AppendLine();

                    cursor += TimeSpan.FromSeconds(gapSeconds);
                }
                try
                {
                    var info = await FFmpeg.GetMediaInfo(processedPaths[i]);
                    cursor += info.Duration;
                }
                catch { }
            }

            string finalConcatArgs = new StringBuilder().AppendFormat("-y -safe 0 -f concat -i \"{0}\" -c copy \"{1}\"", manifestPath, targetAudioPath).ToString();

            batchBuilder.AppendFormat(" \"{0}\" {1} ", _resolvedFfmpegPath, finalConcatArgs).AppendLine();

            batchBuilder.AppendLine("echo.");
            batchBuilder.AppendLine("echo ===================================================");
            batchBuilder.AppendLine("echo       TASKS RUN COMPLETE! INSPECT COMMAND LOGS ABOVE.     ");
            batchBuilder.AppendLine("echo ===================================================");
            batchBuilder.AppendLine("pause");

            await File.WriteAllTextAsync(manualBatchPath, batchBuilder.ToString(), Encoding.UTF8);
        }

        private void ClipboardCopy(string text)
        {
            try
            {
                Clipboard.SetText(text);
                MessageBox.Show("Text copied to clipboard!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy text to clipboard: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}


