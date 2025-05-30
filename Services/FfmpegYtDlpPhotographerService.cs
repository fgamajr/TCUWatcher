using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Globalization; // For TimeSpan formatting and double.Parse
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services
{
    public class FfmpegYtDlpPhotographerService : IPhotographerService
    {
        private readonly ILogger<FfmpegYtDlpPhotographerService> _logger;
        private readonly string _ffmpegPath; 
        private readonly string _ytDlpPath; 
        private readonly string _snapshotFileExtension;
        private readonly string _ffmpegSnapshotOutputCodec;
        private readonly int _commandTimeoutMilliseconds;

        public FfmpegYtDlpPhotographerService(IConfiguration configuration, ILogger<FfmpegYtDlpPhotographerService> logger)
        {
            _logger = logger;

            string? configuredFfmpegPath = configuration["Photographer:FfmpegPath"];
            if (string.IsNullOrWhiteSpace(configuredFfmpegPath))
            {
                _ffmpegPath = "ffmpeg"; 
                _logger.LogInformation("Photographer:FfmpegPath not configured or effectively empty, defaulting to 'ffmpeg'.");
            }
            else
            {
                _ffmpegPath = configuredFfmpegPath.Trim(); 
            }

            string? configuredYtDlpPath = configuration["Photographer:YtDlpPath"];
            if (string.IsNullOrWhiteSpace(configuredYtDlpPath))
            {
                _ytDlpPath = "yt-dlp"; 
                _logger.LogInformation("Photographer:YtDlpPath not configured or effectively empty, defaulting to 'yt-dlp'.");
            }
            else
            {
                _ytDlpPath = configuredYtDlpPath.Trim();
            }

            string configuredFormat = configuration["Photographer:Format"]?.ToLowerInvariant() ?? "png";
            if (configuredFormat == "mjpeg" || configuredFormat == "jpg")
            {
                _snapshotFileExtension = "jpg";
                _ffmpegSnapshotOutputCodec = "mjpeg";
            }
            else
            {
                _snapshotFileExtension = "png"; 
                _ffmpegSnapshotOutputCodec = "png";
            }
            _logger.LogInformation("Snapshot format configured: file extension '{FileExt}', ffmpeg codec '{FfmpegCodec}'.", _snapshotFileExtension, _ffmpegSnapshotOutputCodec);
            
            _commandTimeoutMilliseconds = configuration.GetValue<int?>("Photographer:CommandTimeoutMilliseconds") ?? 30000;
        }

        public async Task<byte[]?> TakeSnapshotAsync(string videoUrl, string liveEventId)
        {
            _logger.LogInformation("Attempting to take snapshot for LiveEvent: {LiveEventId}, URL: {VideoUrl}", liveEventId, videoUrl);
            string streamUrlToUse = videoUrl;

            _logger.LogDebug("Using yt-dlp to resolve stream URL for {VideoUrl}", videoUrl);
            string ytDlpArgs = $"-g --no-warnings --socket-timeout 10 \"{videoUrl}\"";
            ProcessStartInfo ytDlpPsi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = ytDlpArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            StringBuilder ytDlpOutputBuilder = new StringBuilder();
            StringBuilder ytDlpErrorBuilder = new StringBuilder();
            int ytDlpExitCode = -1;

            try
            {
                using Process ytDlpProcess = new Process { StartInfo = ytDlpPsi };
                ytDlpProcess.OutputDataReceived += (sender, e) => { if (e.Data != null) ytDlpOutputBuilder.AppendLine(e.Data); };
                ytDlpProcess.ErrorDataReceived += (sender, e) => { if (e.Data != null) ytDlpErrorBuilder.AppendLine(e.Data); };

                ytDlpProcess.Start();
                ytDlpProcess.BeginOutputReadLine();
                ytDlpProcess.BeginErrorReadLine();
                
                bool exited = await Task.Run(() => ytDlpProcess.WaitForExit(_commandTimeoutMilliseconds / 2 < 15000 ? _commandTimeoutMilliseconds / 2 : 15000));

                if (!exited)
                {
                    ytDlpProcess.Kill(true);
                    _logger.LogWarning("yt-dlp process timed out for {VideoUrl}", videoUrl);
                }
                else
                {
                    ytDlpExitCode = ytDlpProcess.ExitCode;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception running yt-dlp for {VideoUrl}", videoUrl);
            }
            
            string ytDlpStdOut = ytDlpOutputBuilder.ToString().Trim();
            string ytDlpStdErr = ytDlpErrorBuilder.ToString().Trim();

            if (ytDlpExitCode == 0 && !string.IsNullOrWhiteSpace(ytDlpStdOut))
            {
                streamUrlToUse = ytDlpStdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? streamUrlToUse;
                _logger.LogInformation("yt-dlp successfully resolved {VideoUrl} to {StreamUrlToUse}", videoUrl, streamUrlToUse);
                if (!string.IsNullOrEmpty(ytDlpStdErr))
                {
                     _logger.LogWarning("yt-dlp stderr for {VideoUrl} (though successful):\n{YtDlpStdErr}", videoUrl, ytDlpStdErr);
                }
            }
            else
            {
                _logger.LogWarning("yt-dlp failed or returned no URL for {VideoUrl}. ExitCode: {ExitCode}. Stdout: '{YtDlpStdOut}'. Stderr: '{YtDlpStdErr}'. Will attempt ffmpeg with original URL: {OriginalUrl}", 
                    videoUrl, ytDlpExitCode, ytDlpStdOut, ytDlpStdErr, videoUrl);
                streamUrlToUse = videoUrl; 
            }
            
            string ffmpegArgs = $"-nostdin -i \"{streamUrlToUse}\" -frames:v 1 -f image2pipe -vcodec {_ffmpegSnapshotOutputCodec} pipe:1";
            _logger.LogDebug("Executing ffmpeg for LiveEvent {LiveEventId} with resolved URL: {StreamUrlToUse} and args: {Args}", liveEventId, streamUrlToUse, ffmpegArgs);

            return await ExecuteFfmpegToPipeAsync(ffmpegArgs, liveEventId, streamUrlToUse, "live snapshot");
        }

        public async Task<byte[]?> TakeSnapshotFromFileAsync(string localFilePath, string liveEventId, TimeSpan timeOffset)
        {
            _logger.LogInformation("Attempting snapshot from file for LiveEvent: {LiveEventId}, File: {LocalFilePath}, Offset: {TimeOffset}", 
                                liveEventId, localFilePath, timeOffset);

            string timeOffsetFormatted = $"{timeOffset.Hours:D2}:{timeOffset.Minutes:D2}:{timeOffset.Seconds:D2}.{timeOffset.Milliseconds:D3}";
            string ffmpegArgs = $"-nostdin -ss {timeOffsetFormatted} -i \"{localFilePath}\" -frames:v 1 -f image2pipe -vcodec {_ffmpegSnapshotOutputCodec} pipe:1";
            
            _logger.LogDebug("Executing ffmpeg for file snapshot (Event: {LiveEventId}): {Args}", liveEventId, ffmpegArgs);
            return await ExecuteFfmpegToPipeAsync(ffmpegArgs, liveEventId, localFilePath, "file snapshot");
        }

        public async Task<string?> ExtractFullAudioAsync(string localFilePath, string liveEventId, string outputAudioDirectory, string audioFileExtensionWithoutDot)
        {
            _logger.LogInformation("Attempting to extract full audio for LiveEvent: {LiveEventId}, File: {LocalFilePath}, TargetDir: {OutputAudioDirectory}, Ext: {AudioExt}", 
                                liveEventId, localFilePath, outputAudioDirectory, audioFileExtensionWithoutDot);
            
            try
            {
                Directory.CreateDirectory(outputAudioDirectory);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Failed to create output directory for audio extraction: {OutputAudioDirectory}", outputAudioDirectory);
                return null;
            }

            string audioFileName = $"audio_{liveEventId}.{audioFileExtensionWithoutDot.TrimStart('.')}";
            string outputAudioPath = Path.Combine(outputAudioDirectory, audioFileName);

            string ffmpegArgs = $"-nostdin -y -i \"{localFilePath}\" -vn -c:a aac -b:a 128k \"{outputAudioPath}\"";
            _logger.LogDebug("Executing ffmpeg for audio extraction (Event: {LiveEventId}): {Args}", liveEventId, ffmpegArgs);

            ProcessStartInfo psi = new ProcessStartInfo(_ffmpegPath, ffmpegArgs)
            {
                RedirectStandardOutput = true, 
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using Process process = new Process { StartInfo = psi };
                var errorBuffer = new StringBuilder();
                var outputBuffer = new StringBuilder(); 
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuffer.AppendLine(e.Data); };
                process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuffer.AppendLine(e.Data); };
                
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                bool exited = await Task.Run(() => process.WaitForExit(_commandTimeoutMilliseconds * 20)); 

                if (!exited)
                {
                    process.Kill(true);
                    _logger.LogError("ffmpeg (audio extraction) timed out for {LiveEventId}, File: {LocalFilePath}", liveEventId, localFilePath);
                    TryDeleteFile(outputAudioPath);
                    return null;
                }

                string errors = errorBuffer.ToString();
                string stdOut = outputBuffer.ToString();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("ffmpeg (audio extraction) failed for {LiveEventId}, File: {LocalFilePath}. ExitCode: {ExitCode}. Stdout: {StdOut}\nErrors:\n{Errors}", 
                                     liveEventId, localFilePath, process.ExitCode, stdOut, errors);
                    TryDeleteFile(outputAudioPath);
                    return null;
                }

                if (!File.Exists(outputAudioPath) || new FileInfo(outputAudioPath).Length == 0)
                {
                    _logger.LogWarning("ffmpeg (audio extraction) completed but output file is missing or empty for {LiveEventId}. Path: {OutputAudioPath}. Stdout: {StdOut}\nErrors:\n{Errors}", 
                                       liveEventId, outputAudioPath, stdOut, errors);
                    TryDeleteFile(outputAudioPath);
                    return null;
                }

                _logger.LogInformation("Full audio extracted successfully for {LiveEventId} to {OutputAudioPath}", liveEventId, outputAudioPath);
                return outputAudioPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during audio extraction for {LiveEventId}, File: {LocalFilePath}", liveEventId, localFilePath);
                TryDeleteFile(outputAudioPath);
                return null;
            }
        }

        private async Task<byte[]?> ExecuteFfmpegToPipeAsync(string ffmpegArgs, string logContextId, string inputIdentifier, string operationType)
        {
            ProcessStartInfo psi = new ProcessStartInfo(_ffmpegPath, ffmpegArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using Process process = new Process { StartInfo = psi };
                var outputBuffer = new MemoryStream();
                var errorBuffer = new StringBuilder();

                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuffer.AppendLine(e.Data); };

                process.Start();
                process.BeginErrorReadLine();

                await process.StandardOutput.BaseStream.CopyToAsync(outputBuffer);
                
                bool exited = await Task.Run(() => process.WaitForExit(_commandTimeoutMilliseconds));

                if (!exited)
                {
                    process.Kill(true); 
                    _logger.LogError("ffmpeg ({OperationType}) timed out for {LogContextId}, Input: {InputIdentifier}", operationType, logContextId, inputIdentifier);
                    return null;
                }
                
                string errors = errorBuffer.ToString();
                if (process.ExitCode != 0)
                {
                    _logger.LogError("ffmpeg ({OperationType}) failed for {LogContextId}, Input: {InputIdentifier}. ExitCode: {ExitCode}. Errors:\n{Errors}", 
                                     operationType, logContextId, inputIdentifier, process.ExitCode, errors);
                    return null;
                }

                if (outputBuffer.Length == 0)
                {
                    _logger.LogWarning("ffmpeg ({OperationType}) produced no output for {LogContextId}, Input: {InputIdentifier}. Errors (if any):\n{Errors}", 
                                       operationType, logContextId, inputIdentifier, errors);
                    return null;
                }
                
                _logger.LogInformation("ffmpeg ({OperationType}) successful for {LogContextId}. Data size: {Size}", operationType, logContextId, outputBuffer.Length);
                return outputBuffer.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during ffmpeg ({OperationType}) for {LogContextId}, Input: {InputIdentifier}", operationType, logContextId, inputIdentifier);
                return null;
            }
        }
        
        private void TryDeleteFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Cleaned up potentially partial file: {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up file: {FilePath}", filePath);
            }
        }
    } // End of FfmpegYtDlpPhotographerService class
} // End of namespace