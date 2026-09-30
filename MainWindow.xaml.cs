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
        public class SubtitleBlock
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string Text { get; set; }
            public double TargetDuration => (End - Start).TotalSeconds;
        }

        public MainWindow()
        {
            InitializeComponent();
            FFmpeg.SetExecutablesPath(AppDomain.CurrentDomain.BaseDirectory);
            EnsureIgnoreFileExists();
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
                string ffmpegExecutableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegExecutableName,
                    Arguments = $"-y -i \"{mp4Path}\" -vn -acodec pcm_s16le -ac 1 -ar 16000 \"{tempWavPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!File.Exists(psi.FileName) && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe")))
                {
                    psi.FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                }

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (process != null)
                    {
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45));
                        var processTask = process.WaitForExitAsync();
                        var completedTask = await Task.WhenAny(processTask, timeoutTask);
                        if (completedTask == timeoutTask)
                        {
                            try { process.Kill(); } catch { }
                            throw new TimeoutException("FFmpeg operation timed out. Ensure the MP4 video is not corrupted.");
                        }
                    }
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

                            // 1. Assign values calculated from video analysis
                            SldRate.Value = 0;
                            SldPitch.Value = Math.Clamp((int)pitchOffsetPercent, -25, 25);
                        }
                        else
                        {
                            CboVoice.SelectedIndex = 1;
                            double pitchOffsetPercent = ((averagePitchHz - 210) / 210) * 100;

                            // 1. Assign values calculated from video analysis
                            SldRate.Value = 0;
                            SldPitch.Value = Math.Clamp((int)pitchOffsetPercent, -25, 25);
                        }

                        // 2. Lock sliders so the user cannot accidentally modify the matched sync baseline
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
                MessageBox.Show("Please open a file or enter some Khmer text first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(outputName))
            {
                MessageBox.Show("Please type a valid filename.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Fixed character conversion engine logic to clear string replace bugs
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
                        // Flush any previously accumulated text before starting a new block
                        if (readingText && textAccumulator.Length > 0)
                        {
                            subBlocks.Add(new SubtitleBlock { Start = currentStart, End = currentEnd, Text = textAccumulator.ToString().Trim() });
                            textAccumulator.Clear();
                        }

                        var parts = Regex.Split(trimmedLine, @"\s*-->\s*");
                        if (parts.Length >= 2)
                        {
                            TimeSpan.TryParse(parts[0].Replace(',', '.'), out currentStart);
                            TimeSpan.TryParse(parts[1].Replace(',', '.'), out currentEnd);
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

                // Flush the very last subtitle block
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

                string rateValue = SldRate.Value >= 0 ? $"+{(int)SldRate.Value}%" : $"{(int)SldRate.Value}%";
                string pitchValue = SldPitch.Value >= 0 ? $"+{(int)SldPitch.Value}%" : $"{(int)SldPitch.Value}%";

                // Create environment to dump processed fragment files
                string tempDir = Path.Combine(Path.GetTempPath(), "KhmerTTS_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                List<string> processedPaths = new List<string>();

                // 1. Render individual files and dynamically stretch tempo
                for (int i = 0; i < subBlocks.Count; i++)
                {
                    string rawSeg = Path.Combine(tempDir, $"raw_{i}.mp3");
                    string fixedSeg = Path.Combine(tempDir, $"fixed_{i}.mp3");

                    //  This satisfies the exact two-string argument requirement
                    await voice.SaveAudioToFile(subBlocks[i].Text, rawSeg);


                    var info = await FFmpeg.GetMediaInfo(rawSeg);
                    double duration = info.Duration.TotalSeconds;

                    if (duration > subBlocks[i].TargetDuration && subBlocks[i].TargetDuration > 0)
                    {
                        // Calculate the baseline automatic speed stretch factor required for timeline sync
                        double speedRatio = 1.0;
                        if (duration > subBlocks[i].TargetDuration && subBlocks[i].TargetDuration > 0)
                        {
                            speedRatio = duration / subBlocks[i].TargetDuration;
                        }

                        // 1. Incorporate your read-only slider configurations set by the video analyzer
                        double sliderSpeedFactor = 1.0 + (SldRate.Value / 100.0);
                        double combinedSpeedRatio = speedRatio * sliderSpeedFactor;

                        // Clamp speed to safe FFmpeg execution limits (0.5 to 2.0)
                        if (combinedSpeedRatio > 2.0) combinedSpeedRatio = 2.0;
                        if (combinedSpeedRatio < 0.5) combinedSpeedRatio = 0.5;

                        // 2. Process pitch mathematically via audio frequency shifts
                        double pitchFactor = 1.0 + (SldPitch.Value / 100.0);
                        int targetSampleRate = 24000; // EdgeTTS base sample rate
                        int adjustedSampleRate = (int)(targetSampleRate * pitchFactor);

                        // 3. Chain filters: Apply pitch adjustments AND speed modifications globally
                        string args = $"-y -i \"{rawSeg}\" -filter:a \"asetrate={adjustedSampleRate},aresample={targetSampleRate},atempo={combinedSpeedRatio:F2}\" \"{fixedSeg}\"";

                        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ffmpeg.exe",
                            Arguments = args,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        process?.WaitForExit();

                    }
                    else
                    {
                        File.Copy(rawSeg, fixedSeg, true);
                    }

                    processedPaths.Add(fixedSeg);
                }

                // 2. Build text manifest to assemble clips on the absolute timeline
                string manifestPath = Path.Combine(tempDir, "concat_list.txt");
                StringBuilder manifestBuilder = new StringBuilder();
                TimeSpan timelineCursor = TimeSpan.Zero;

                for (int i = 0; i < processedPaths.Count; i++)
                {
                    // Add silent audio gaps between blocks if a delay exists
                    if (subBlocks[i].Start > timelineCursor)
                    {
                        double gapSeconds = (subBlocks[i].Start - timelineCursor).TotalSeconds;
                        string silenceFile = Path.Combine(tempDir, $"silence_{i}.mp3");
                        string silenceArgs = $"-y -f lavfi -i anullsrc=r=44100:c=stereo -t {gapSeconds:F3} -c:a mp3 \"{silenceFile}\"";

                        var pSilence = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ffmpeg.exe",
                            Arguments = silenceArgs,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        pSilence?.WaitForExit();

                        manifestBuilder.AppendLine($"file '{silenceFile.Replace("\\", "/")}'");
                        timelineCursor += TimeSpan.FromSeconds(gapSeconds);
                    }

                    manifestBuilder.AppendLine($"file '{processedPaths[i].Replace("\\", "/")}'");
                    var info = await FFmpeg.GetMediaInfo(processedPaths[i]);
                    timelineCursor += info.Duration;
                }

                await File.WriteAllTextAsync(manifestPath, manifestBuilder.ToString(), Encoding.UTF8);

                // 3. Concat everything into the final target audio file path
                string concatArgs = $"-y -f concat -safe 0 -i \"{manifestPath}\" -c copy \"{targetAudioPath}\"";
                var pConcat = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg.exe",
                    Arguments = concatArgs,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                pConcat?.WaitForExit();

                // 4. Safely clean up workspace files
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }


                await File.WriteAllTextAsync(targetSubtitlePath, rawText, Encoding.UTF8);
                Mouse.OverrideCursor = null;
                MessageBox.Show("Files generated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                string argument = $"/select,\"{targetAudioPath}\"";
                System.Diagnostics.Process.Start("explorer.exe", argument);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show($"Could not export assets: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                BtnGenerate.IsEnabled = true;
                BtnOpenFile.IsEnabled = true;
                BtnGenerate.Content = "Generate MP3";
            }

        } // Closes BtnGenerate_Click
          // NEW FEATURE: Natively multiplexes the new Khmer MP3 track directly back into an MP4 video layout
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
                string ffmpegExecutableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ffmpegExecutableName);
                if (!File.Exists(ffmpegPath)) ffmpegPath = ffmpegExecutableName;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    // REMOVED: Accidental escape backslash before the second input file path
                    Arguments = $"-y -i \"{videoDialog.FileName}\" -i \"{audioDialog.FileName}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -shortest \"{outputDialog.FileName}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (process != null) await process.WaitForExitAsync();
                }

                Mouse.OverrideCursor = null;
                MessageBox.Show("Khmer voice audio merged into the MP4 video asset successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                string argument = $"/select,\"{outputDialog.FileName}\"";
                System.Diagnostics.Process.Start("explorer.exe", argument);
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

            // Unlock and reset
            SldRate.Value = 0;
            SldPitch.Value = 0;
            SldRate.IsEnabled = true;
            SldPitch.IsEnabled = true;

            CboVoice.SelectedIndex = 0;
        }

    }
}