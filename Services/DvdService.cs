using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DvdRipper.Models;

namespace DvdRipper.Services
{
    /// <summary>
    /// Encapsulates the command‑line interactions required to inspect and rip a DVD.
    /// </summary>
    public class DvdService
    {
        private const string LsdvdPath = "lsdvd";
        private const string MpvPath = "mpv";
        private const string MplayerPath = "mplayer";
        private const string MkvMergePath = "mkvmerge";
        private const string FfmpegPath = "ffmpeg";

        // New helper: prefix log messages with a level (INFO/DEBUG/ERROR)
        private static void Report(IProgress<string>? log, string message, string level = "INFO")
        {
            log?.Report($"[{level}] {message}\n");
        }


        /// <summary>
        /// Scans the disc in <paramref name="device"/> for titles using lsdvd. If lsdvd
        /// reports unknown durations or fails, falls back to probing with mplayer.
        /// </summary>
        public async Task<List<TitleInfo>> ScanTitlesAsync(string device, IProgress<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("Device must be provided.", nameof(device));

            Report(log, $"Scanning titles on {device}…\n");
            var titles = new List<TitleInfo>();

            var cmd = $"{LsdvdPath} -Ox {device}";
            Report(log, $"Running: {cmd}", "DEBUG");
            try
            {
                // Attempt to parse lsdvd XML output for accurate durations.
                var xml = await RunProcessWithOutputAsync(LsdvdPath, $"-Ox {device}");
                Report(log, "lsdvd returned XML; parsing…", "DEBUG");
                if (!string.IsNullOrWhiteSpace(xml))
                {
                    var doc = XDocument.Parse(xml);
                    foreach (var track in doc.Descendants("track"))
                    {
                        var ixAttr = track.Element("ix")?.Value;
                        var lenAttr = track.Element("length")?.Value;
                        if (int.TryParse(ixAttr, out int ix))
                        {
                            int lenSeconds = 0;
                            if (!string.IsNullOrWhiteSpace(lenAttr))
                            {
                                // length attribute may be floating point seconds.
                                if (double.TryParse(lenAttr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                                {
                                    lenSeconds = (int)Math.Round(d);
                                }
                            }
                            titles.Add(new TitleInfo(ix, lenSeconds));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Report(log, $"lsdvd error: {ex.Message}", "ERROR");
            }
            if (titles.Count == 0)
            {
                // Fallback: probe first 50 titles with mplayer.
                Report(log, "lsdvd failed or returned no titles; probing with mplayer…\n");
                for (int i = 1; i <= 50; i++)
                {
                    var mplayerArgs =
                        $"-really-quiet -identify -frames 0 -vo null -ao null -dvd-device {device} dvd://{i}";
                    Report(log, $"Running mplayer probe: mplayer {mplayerArgs}", "DEBUG");
                    try
                    {
                        var identify = await RunProcessWithOutputAsync(MplayerPath, $"-really-quiet -identify -frames 0 -vo null -ao null -dvd-device {device} dvd://{i}");
                        if (!string.IsNullOrWhiteSpace(identify))
                        {
                            // Parse ID_LENGTH= in seconds (floating point) and count as valid only if length > 0.
                            var lines = identify.Split('\n');
                            foreach (var line in lines)
                            {
                                if (line.StartsWith("ID_LENGTH="))
                                {
                                    var val = line.Substring("ID_LENGTH=".Length);
                                    if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs))
                                    {
                                        int sec = (int)Math.Round(secs);
                                        if (sec > 0)
                                        {
                                            titles.Add(new TitleInfo(i, sec));
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Report(log, $"mplayer error: {ex.Message}", "ERROR");
                        // ignore errors probing individual titles
                    }
                }
            }
            // Order by number and return
            titles = titles.OrderBy(t => t.Number).ToList();
            Report(log, $"Found {titles.Count} title(s).\n");
            return titles;
        }

        /// <summary>
        /// Rips the specified <paramref name="titleNumber"/> from <paramref name="device"/> to <paramref name="outputPath"/>.
        /// Uses mpv for progress reporting and falls back to mplayer if mpv stalls.
        /// Progress is reported via <paramref name="progress"/> (percentage 0–100) and log messages via <paramref name="log"/>.
        /// </summary>
        public async Task RipAsync(string device, int titleNumber, string outputPath, IProgress<double>? progress = null, IProgress<string>? log = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("Device must be provided.", nameof(device));
            if (titleNumber <= 0) throw new ArgumentOutOfRangeException(nameof(titleNumber));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path must be provided.", nameof(outputPath));

            var rawFile = Path.Combine(Path.GetTempPath(), $"dvd_title{titleNumber}_{Guid.NewGuid():N}.vob");
            try
            {
                Report(log, $"Ripping title {titleNumber} from {device}…\n");
                // Start mpv dumping process.
                var mpvArgs = string.Join(' ', new[]
                {
                    "--no-config",
                    "--really-quiet",
                    "--ao=null",
                    "--vo=null",
                    "--term-osd=force",
                    "--term-status-msg=[progress] ${percent-pos}% (${time-pos}/${duration})",
                    "--idle=no",
                    "--loop-file=no",
                    $"--dvd-device {device}",
                    $"dvd://{titleNumber}",
                    $"--stream-record={rawFile}"
                });
                Report(log, $"Starting mpv with args: {mpvArgs}", "DEBUG");

                Process? mpv = null;
                try
                {

                    mpv = new System.Diagnostics.Process();
                    mpv.StartInfo.FileName = MpvPath;
                    mpv.StartInfo.Arguments = mpvArgs;
                    mpv.StartInfo.RedirectStandardError = true;
                    mpv.StartInfo.UseShellExecute = false;
                    mpv.EnableRaisingEvents = true;

                    // We'll monitor bytes written for stall detection.
                    long lastBytes = 0;
                    var progressPattern = new Regex(@"\[progress\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
                    var lastUpdateTime = DateTime.UtcNow;

                    mpv.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        // Parse mpv progress output
                        var match = progressPattern.Match(e.Data);
                        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct))
                        {
                            progress?.Report(pct);
                            lastUpdateTime = DateTime.UtcNow;
                        }
                        // Write log lines for debugging/troubleshooting.
                        Report(log, $"mpv: {e.Data}", "DEBUG");
                    };

                    mpv.Start();
                    mpv.BeginErrorReadLine();

                    // Monitor progress to detect stalls.
                    while (!mpv.HasExited)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(500, cancellationToken);
                        // Check file size
                        if (File.Exists(rawFile))
                        {
                            long size = new FileInfo(rawFile).Length;
                            if (size > lastBytes)
                            {
                                lastBytes = size;
                                lastUpdateTime = DateTime.UtcNow;
                            }
                        }
                        // If no progress for 10 seconds (neither progress update nor file growth) then kill and fallback
                        if ((DateTime.UtcNow - lastUpdateTime).TotalSeconds > 10)
                        {
                            Report(log, "mpv appears stalled; falling back to mplayer…\n");
                            try { mpv.Kill(); } catch { }
                            break;
                        }
                    }
                    if (!mpv.HasExited)
                    {
                        // Wait for graceful exit
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(500, cancellationToken);
                        await mpv.WaitForExitAsync(cancellationToken);
                    }

                    Report(log, $"mpv exit code: {mpv.ExitCode}", "DEBUG");
                }
                catch (OperationCanceledException)
                {
                    // Cancelled: ensure the process is terminated
                    try { if (mpv != null && !mpv.HasExited) mpv.Kill(); } catch { }
                    throw;  // rethrow so caller knows it was cancelled
                }


                // If file still empty, fallback to mplayer
                if (!File.Exists(rawFile) || new FileInfo(rawFile).Length == 0)
                {
                    Report(log, "mpv produced no output; switching to mplayer…", "INFO");
                    await RunMplayerDumpAsync(device, titleNumber, rawFile, log, cancellationToken);
                }

                // Remux to MKV
                await RemuxAsync(rawFile, outputPath, log, cancellationToken);
            }
            finally
            {
                // Clean up temporary file.
                try
                {
                    if (File.Exists(rawFile)) File.Delete(rawFile);
                }
                catch { }
            }
        }

        private async Task RunMplayerDumpAsync(string device, int title, string rawFile, IProgress<string>? log, CancellationToken cancellationToken)
        {
            Report(log, $"Running mplayer fallback for title {title}…\n");
            var args = $"-really-quiet -ao null -vo null -nocache -dvd-device {device} dvd://{title} -dumpstream -dumpfile {rawFile}";
            Report(log, $"Running mplayer dump: mplayer {args}", "DEBUG");
            var output = await RunProcessWithOutputAsync(MplayerPath, args, log, cancellationToken);
            Report(log, "mplayer finished dumping.", "DEBUG");
        }

        private async Task RemuxAsync(string rawFile, string outputPath, IProgress<string>? log, CancellationToken cancellationToken)
        {
            // Ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            Report(log, $"Remuxing to {outputPath}…\n");
            // Try mkvmerge first
            try
            {
                var mkvArgs = $"-q -o {EscapeArg(outputPath)} {EscapeArg(rawFile)}";
                Report(log, $"Running mkvmerge: mkvmerge {mkvArgs}", "DEBUG");
                try
                {
                    await RunProcessAsync(MkvMergePath, mkvArgs, log, cancellationToken);
                    Report(log, "mkvmerge completed successfully.", "INFO");
                    return;
                }
                catch (Exception ex)
                {
                    Report(log, $"mkvmerge error: {ex.Message}", "ERROR");
                }
            }
            catch
            {
                Report(log, "mkvmerge failed; falling back to ffmpeg…\n");
            }
            // ffmpeg fallback
            var ffArgs = $"-hide_banner -loglevel warning -fflags +genpts+igndts -avoid_negative_ts make_zero -muxpreload 0 -muxdelay 0 -i {EscapeArg(rawFile)} -map 0:v:0 -map 0:a -map 0:s? -c copy {EscapeArg(outputPath)}";
            Report(log, $"Running ffmpeg: ffmpeg {ffArgs}", "DEBUG");
            await RunProcessAsync(FfmpegPath, ffArgs, log, cancellationToken);
            Report(log, "ffmpeg remux completed successfully.", "INFO");
        }

        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "";
            return '"' + arg.Replace("\"", "\\\"") + '"';
        }

        private async Task<string> RunProcessWithOutputAsync(string fileName, string args, IProgress<string>? log = null, CancellationToken cancellationToken = default)
        {
            Report(log, $"Spawning: {fileName} {args}", "DEBUG");
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = fileName;
            proc.StartInfo.Arguments = args;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.CreateNoWindow = true;

            // Kill the process if the token is cancelled
            cancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            });

            proc.EnableRaisingEvents = true;
            proc.Start();
            var stdOut = new List<string>();
            var stdErr = new List<string>();
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdOut.Add(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stdErr.Add(e.Data); Report(log, e.Data + "\n"); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(cancellationToken);
            Report(log, $"{fileName} exited with code {proc.ExitCode}", "DEBUG");
            if (proc.ExitCode != 0)
            {
                // include stderr in exception message
                throw new InvalidOperationException(string.Join("\n", stdErr));
            }
            return string.Join("\n", stdOut);
        }

        private async Task RunProcessAsync(string fileName, string args, IProgress<string>? log = null, CancellationToken cancellationToken = default)
        {
            Report(log, $"Spawning: {fileName} {args}", "DEBUG");
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = fileName;
            proc.StartInfo.Arguments = args;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.CreateNoWindow = true;
            proc.EnableRaisingEvents = true;
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Report(log, e.Data + "\n"); };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) Report(log, e.Data + "\n"); };

            cancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            });

            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync(cancellationToken);
            Report(log, $"{fileName} exited with code {proc.ExitCode}", "DEBUG");
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"Process {fileName} exited with code {proc.ExitCode}.");
            }
        }
    }
}