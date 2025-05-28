using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq; // Added for .FirstOrDefault()
using System.Text;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services
{
    public class FfmpegYtDlpPhotographerService : IPhotographerService
    {
        private readonly ILogger<FfmpegYtDlpPhotographerService> _logger;
        private readonly string? _ffmpegPath;
        private readonly string? _ytDlpPath;
        private readonly string _snapshotFormat;
        private readonly int _commandTimeoutMilliseconds;

        public FfmpegYtDlpPhotographerService(IConfiguration configuration, ILogger<FfmpegYtDlpPhotographerService> logger)
        {
            _logger = logger;
            _ffmpegPath = configuration["Photographer:FfmpegPath"] ?? "ffmpeg";
            _ytDlpPath = configuration["Photographer:YtDlpPath"] ?? "yt-dlp";
            _snapshotFormat = configuration["Photographer:Format"] ?? "png";
            _commandTimeoutMilliseconds = configuration.GetValue<int?>("Photographer:CommandTimeoutMilliseconds") ?? 30000;
        }

        public async Task<byte[]?> TakeSnapshotAsync(string videoUrl, string liveEventId)
        {
            _logger.LogInformation("Attempting to take snapshot for LiveEvent: {LiveEventId}, URL: {VideoUrl}", liveEventId, videoUrl);

            string streamUrlToUse = videoUrl;

            if (!string.IsNullOrEmpty(_ytDlpPath))
            {
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

                    // Give yt-dlp part of the total timeout, e.g., half or a fixed amount like 15s
                    bool exited = await Task.Run(() => ytDlpProcess.WaitForExit(_commandTimeoutMilliseconds / 2 < 15000 ? _commandTimeoutMilliseconds / 2 : 15000)); 

                    if (!exited)
                    {
                        ytDlpProcess.Kill(true); // Kill entire process tree
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
                    _logger.LogWarning("yt-dlp failed or returned no URL for {VideoUrl}. ExitCode: {ExitCode}. Stdout: '{YtDlpStdOut}'. Stderr: '{YtDlpStdErr}'. Will attempt ffmpeg with original URL.", 
                        videoUrl, ytDlpExitCode, ytDlpStdOut, ytDlpStdErr);
                }
            }
            else
            {
                _logger.LogDebug("yt-dlp path not configured or empty. Skipping yt-dlp pre-processing.");
            }

            string ffmpegArgs = $"-nostdin -i \"{streamUrlToUse}\" -frames:v 1 -f image2pipe -vcodec {_snapshotFormat} pipe:1";
            _logger.LogDebug("Executing ffmpeg for LiveEvent {LiveEventId} with resolved URL: {StreamUrlToUse} and args: {Args}", liveEventId, streamUrlToUse, ffmpegArgs);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = ffmpegArgs,
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
                    process.Kill(true); // Kill entire process tree
                    _logger.LogError("ffmpeg process timed out after {Timeout}ms for LiveEvent {LiveEventId}, URL: {StreamUrl}.", _commandTimeoutMilliseconds, liveEventId, streamUrlToUse);
                    return null;
                }
                
                string errors = errorBuffer.ToString();
                if (process.ExitCode != 0)
                {
                    _logger.LogError("ffmpeg failed for LiveEvent {LiveEventId}, URL: {StreamUrl}. ExitCode: {ExitCode}. Errors:\n{Errors}", liveEventId, streamUrlToUse, process.ExitCode, errors);
                    return null;
                }

                if (outputBuffer.Length == 0)
                {
                    _logger.LogWarning("ffmpeg produced no output for LiveEvent {LiveEventId}, URL: {StreamUrl}. Errors:\n{Errors}", liveEventId, streamUrlToUse, errors);
                    return null;
                }

                _logger.LogInformation("Snapshot captured successfully for LiveEvent {LiveEventId} using URL {StreamUrlToUse}. Size: {Size} bytes.", liveEventId, streamUrlToUse, outputBuffer.Length);
                return outputBuffer.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while taking snapshot for LiveEvent {LiveEventId}, URL: {StreamUrl}.", liveEventId, streamUrlToUse);
                return null;
            }
        } // End of TakeSnapshotAsync method
    } // End of FfmpegYtDlpPhotographerService class
} // End of namespace
