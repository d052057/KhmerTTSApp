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
                // Added missing <string> type definition
                List<string> wordsToIgnore = LoadIgnoreWords();
                string[] lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                StringBuilder cleanAudioTextBuilder = new StringBuilder();
                string lastAddedText = string.Empty;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Regex.IsMatch(trimmedLine, @"^\d+$")) continue;
                    if (trimmedLine.Contains("-->")) continue;

                    if (!string.IsNullOrWhiteSpace(trimmedLine))
                    {
                        foreach (string customWord in wordsToIgnore)
                        {
                            trimmedLine = Regex.Replace(trimmedLine, Regex.Escape(customWord), "", RegexOptions.IgnoreCase);
                        }

                        trimmedLine = trimmedLine.Replace("\"", "").Replace("'", "").Trim();

                        if (trimmedLine.Length > 0)
                        {
                            if (trimmedLine.Equals(lastAddedText, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (!string.IsNullOrEmpty(lastAddedText) && trimmedLine.StartsWith(lastAddedText, StringComparison.OrdinalIgnoreCase))
                            {
                                int lastLen = lastAddedText.Length + 1;
                                if (cleanAudioTextBuilder.Length >= lastLen)
                                {
                                    cleanAudioTextBuilder.Length -= lastLen;
                                }
                            }

                            cleanAudioTextBuilder.Append(trimmedLine).Append(" ");
                            lastAddedText = trimmedLine;
                        }
                    }
                }

                string filteredVoiceText = cleanAudioTextBuilder.ToString().Trim();
                filteredVoiceText = Regex.Replace(filteredVoiceText, @"\s+", " ");

                if (string.IsNullOrWhiteSpace(filteredVoiceText))
                {
                    throw new Exception("All text inside this file matched your 'ignore_words.txt' list or timestamp layers.");
                }

                string selectedVoiceName = CboVoice.SelectedIndex == 0 ? "km-KH-PisethNeural" : "km-KH-SreymomNeural";
                var voice = await EdgeTts.GetVoice(selectedVoiceName);

                string rateValue = SldRate.Value >= 0 ? $"+{(int)SldRate.Value}%" : $"{(int)SldRate.Value}%";
                string pitchValue = SldPitch.Value >= 0 ? $"+{(int)SldPitch.Value}%" : $"{(int)SldPitch.Value}%";

                await voice.SaveAudioToFile(filteredVoiceText, targetAudioPath);
                await File.WriteAllTextAsync(targetSubtitlePath, rawText, Encoding.UTF8);

                Mouse.OverrideCursor = null;
                MessageBox.Show("Files generated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Fixed literal string quotes collision format
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
                    // -map 0:v takes original video, -map 1:a takes new audio, -c:v copy copies video stream instantly without slow re-encoding
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
            SldRate.Value = 0;
            SldPitch.Value = 0;
            CboVoice.SelectedIndex = 0;
        }
    }
}