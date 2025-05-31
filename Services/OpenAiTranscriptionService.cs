using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TCUWatcher.API.Models;

namespace TCUWatcher.API.Services
{
    public class OpenAiTranscriptionService : ITranscriptionService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenAiTranscriptionService> _logger;
        private readonly string? _openAIApiKey;
        private readonly string _ffmpegPath;
        private const string OpenAiTranscriptionEndpoint = "https://api.openai.com/v1/audio/transcriptions";
        private const long MaxFileSizeBeforeChunking = 24 * 1024 * 1024; // 24MB
        private const int DefaultSegmentMinutes = 15; // Default to 15 minutes per chunk

        public OpenAiTranscriptionService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<OpenAiTranscriptionService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _openAIApiKey = configuration["OpenAI:ApiKey"];
            _ffmpegPath = configuration["Photographer:FfmpegPath"] ?? "ffmpeg";

            if (string.IsNullOrWhiteSpace(_openAIApiKey))
            {
                _logger.LogError("OpenAI API Key is not configured. Please set OpenAI:ApiKey in configuration.");
            }
        }

        public async Task<TranscriptionResult?> TranscribeAudioAsync(string audioFilePath, string liveEventIdForLogging, string? languageCode = "pt")
        {
            if (string.IsNullOrWhiteSpace(_openAIApiKey))
            {
                _logger.LogError("OpenAI API Key not configured. Cannot transcribe for Event: {EventId}", liveEventIdForLogging);
                return null;
            }
            if (!File.Exists(audioFilePath))
            {
                _logger.LogError("Audio file not found: {AudioFilePath} for Event: {EventId}", audioFilePath, liveEventIdForLogging);
                return null;
            }

            var fileInfo = new FileInfo(audioFilePath);
            _logger.LogInformation("Preparing to transcribe audio for Event {EventId}. File: {File}, Size: {SizeMB:F2}MB.",
                liveEventIdForLogging, audioFilePath, fileInfo.Length / (1024.0 * 1024.0));

            if (fileInfo.Length < MaxFileSizeBeforeChunking)
            {
                _logger.LogInformation("Audio file size under limit. Transcribing directly for Event {EventId}.", liveEventIdForLogging);
                return await TranscribeSingleFileAsync(audioFilePath, liveEventIdForLogging, languageCode);
            }
            else
            {
                _logger.LogInformation("Audio file exceeds size limit. Starting chunking process for Event {EventId}.", liveEventIdForLogging);
                string tempChunkParentDir = Path.Combine(Path.GetTempPath(), $"tcu_audio_processing_{Guid.NewGuid()}");
                
                try
                {
                    Directory.CreateDirectory(tempChunkParentDir);
                    _logger.LogDebug("Created temporary processing directory: {ChunkDir}", tempChunkParentDir);

                    // Step 1: Convert original audio to a standardized MP3 (as per N8N workflow)
                    string standardizedMp3Path = Path.Combine(tempChunkParentDir, $"full_audio_{Guid.NewGuid()}.mp3");
                    bool conversionSuccess = await ConvertToStandardMp3Async(audioFilePath, standardizedMp3Path, liveEventIdForLogging);
                    if (!conversionSuccess)
                    {
                        _logger.LogError("Failed to convert audio to MP3: {AudioFilePath} for Event {EventId}", audioFilePath, liveEventIdForLogging);
                        return null; // Exit if conversion fails
                    }

                    // Step 2: Split the standardized MP3 into chunks
                    List<string> chunkPaths = await SplitAudioAsync(standardizedMp3Path, tempChunkParentDir, liveEventIdForLogging, DefaultSegmentMinutes);
                    TryDeleteFile(standardizedMp3Path, "standardized MP3 after splitting"); // Clean up full MP3

                    if (!chunkPaths.Any())
                    {
                        _logger.LogError("Failed to split large audio file: {AudioFilePath} for Event {EventId}. No chunks created.", audioFilePath, liveEventIdForLogging);
                        return null;
                    }

                    _logger.LogInformation("Successfully split audio into {Count} chunks for Event {EventId}. Starting transcription.", chunkPaths.Count, liveEventIdForLogging);

                    var combinedResult = new TranscriptionResult
                    {
                        Text = "",
                        Segments = new List<TranscriptionSegment>(),
                        Language = languageCode,
                        Duration = 0
                    };
                    float cumulativeDurationOffset = 0;

                    foreach (var chunkPath in chunkPaths)
                    {
                        if (!File.Exists(chunkPath)) 
                        { 
                            _logger.LogWarning("Chunk file not found, skipping: {ChunkPath}", chunkPath);
                            continue; 
                        }
                        var chunkTranscriptionResult = await TranscribeSingleFileAsync(chunkPath, liveEventIdForLogging, languageCode, isChunk: true);
                        
                        if (chunkTranscriptionResult != null)
                        {
                            if (!string.IsNullOrWhiteSpace(chunkTranscriptionResult.Text))
                            {
                                combinedResult.Text += chunkTranscriptionResult.Text + " ";
                            }
                            if (chunkTranscriptionResult.Segments != null)
                            {
                                foreach (var segment in chunkTranscriptionResult.Segments)
                                {
                                    segment.Start += cumulativeDurationOffset;
                                    segment.End += cumulativeDurationOffset;
                                    if (segment.Words != null)
                                    {
                                        foreach (var word in segment.Words)
                                        {
                                            word.Start += cumulativeDurationOffset;
                                            word.End += cumulativeDurationOffset;
                                        }
                                    }
                                    combinedResult.Segments.Add(segment);
                                }
                            }
                            cumulativeDurationOffset += chunkTranscriptionResult.Duration; // Use duration from OpenAI's response for the chunk
                        }
                        TryDeleteFile(chunkPath, "audio chunk after transcription");
                    }
                    
                    combinedResult.Duration = cumulativeDurationOffset;
                    combinedResult.Text = combinedResult.Text.Trim();
                    _logger.LogInformation("Finished processing all chunks for Event {EventId}. Combined text length: {Length}, Total processed duration: {Duration}s", 
                        liveEventIdForLogging, combinedResult.Text.Length, combinedResult.Duration);
                    return combinedResult;
                }
                finally
                {
                    TryDeleteDirectory(tempChunkParentDir);
                }
            }
        }

        private async Task<bool> ConvertToStandardMp3Async(string inputAudioPath, string outputMp3Path, string liveEventIdForLogging)
        {
            _logger.LogInformation("Converting {InputAudioPath} to MP3 for Event {EventId}", inputAudioPath, liveEventIdForLogging);
            // ffmpeg -i input.m4a -vn -ar 44100 -ac 2 -b:a 128k output.mp3 (N8N used 192k, 128k is also good)
            string ffmpegArgs = $"-nostdin -hide_banner -loglevel error -i \"{inputAudioPath}\" -vn -ar 44100 -ac 2 -b:a 128k \"{outputMp3Path}\"";
            
            ProcessStartInfo psi = new ProcessStartInfo(_ffmpegPath, ffmpegArgs)
            {
                RedirectStandardError = true, 
                UseShellExecute = false, 
                CreateNoWindow = true
            };

            try
            {
                using Process process = new Process { StartInfo = psi };
                var errorBuffer = new StringBuilder();
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuffer.AppendLine(e.Data); };
                process.Start();
                process.BeginErrorReadLine();
                bool exited = await Task.Run(() => process.WaitForExit(TimeSpan.FromMinutes(10).Milliseconds)); // Timeout for conversion

                if (!exited || process.ExitCode != 0)
                {
                    _logger.LogError("ffmpeg (MP3 conversion) failed for Event {EventId}. Input: {Input}. ExitCode: {ExitCode}. Errors: {Errors}", 
                        liveEventIdForLogging, inputAudioPath, exited ? process.ExitCode : -1, errorBuffer.ToString());
                    TryDeleteFile(outputMp3Path, "failed MP3 conversion output");
                    return false;
                }
                _logger.LogInformation("Successfully converted {InputAudioPath} to {OutputMp3Path} for Event {EventId}", inputAudioPath, outputMp3Path, liveEventIdForLogging);
                return true;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Exception during MP3 conversion for Event {EventId}, Input: {InputAudioPath}", liveEventIdForLogging, inputAudioPath);
                TryDeleteFile(outputMp3Path, "failed MP3 conversion output");
                return false;
            }
        }

        private async Task<List<string>> SplitAudioAsync(string inputMp3Path, string outputChunkDirectory, string liveEventIdForLogging, int segmentMinutes)
        {
            _logger.LogInformation("Splitting audio file: {InputMp3Path} for Event {EventId} into {SegmentMinutes} min chunks in {OutputChunkDirectory}", 
                inputMp3Path, liveEventIdForLogging, segmentMinutes, outputChunkDirectory);
            
            var chunkPaths = new List<string>();
            int segmentSeconds = segmentMinutes * 60;
            string chunkFileNamePattern = Path.Combine(outputChunkDirectory, $"chunk_{liveEventIdForLogging}_%03d.mp3");
            // ffmpeg -i input.mp3 -f segment -segment_time 900 -c copy chunk_%03d.mp3
            string ffmpegArgs = $"-nostdin -hide_banner -loglevel error -i \"{inputMp3Path}\" -f segment -segment_time {segmentSeconds} -c copy -reset_timestamps 1 \"{chunkFileNamePattern}\"";

            _logger.LogDebug("Executing ffmpeg for audio splitting (Event: {EventId}): {FfmpegPath} {Args}", liveEventIdForLogging, _ffmpegPath, ffmpegArgs);

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
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuffer.AppendLine(e.Data); };
                // OutputDataReceived can be used to monitor ffmpeg's progress on segmentation if needed
                
                process.Start();
                process.BeginErrorReadLine();
                // process.BeginOutputReadLine(); // If you want to capture ffmpeg's stdout for segmenting

                bool exited = await Task.Run(() => process.WaitForExit(TimeSpan.FromMinutes(10).Milliseconds)); 

                string errors = errorBuffer.ToString();
                if (!exited || process.ExitCode != 0)
                {
                    _logger.LogError("ffmpeg (audio splitting) failed for Event {EventId}. Input: {Input}. ExitCode: {ExitCode}. Errors: {Errors}", 
                        liveEventIdForLogging, inputMp3Path, exited ? process.ExitCode : -1, errors);
                    return chunkPaths;
                }

                chunkPaths.AddRange(Directory.GetFiles(outputChunkDirectory, $"chunk_{liveEventIdForLogging}_*.mp3").OrderBy(f => f));
                _logger.LogInformation("Audio successfully split into {Count} chunks in {OutputChunkDirectory} for event {EventId}", 
                    chunkPaths.Count, outputChunkDirectory, liveEventIdForLogging);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during audio splitting for Event {EventId}, Input: {InputMp3Path}", liveEventIdForLogging, inputMp3Path);
            }
            return chunkPaths;
        }

        private async Task<TranscriptionResult?> TranscribeSingleFileAsync(string filePath, string liveEventIdForLogging, string? languageCode, bool isChunk = false)
        {
            string logPrefix = isChunk ? "Chunk Transcription" : "Direct Transcription";
            _logger.LogDebug("{LogPrefix}: Sending file {FilePath} to OpenAI for Event {EventId}.", logPrefix, filePath, liveEventIdForLogging);

            try
            {
                var client = _httpClientFactory.CreateClient("OpenAI");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _openAIApiKey);

                await using var fileStream = File.OpenRead(filePath);
                using var content = new MultipartFormDataContent($"----------{Guid.NewGuid()}");
                
                var streamContent = new StreamContent(fileStream);
                // OpenAI docs suggest common audio formats like mp3, mp4, mpeg, mpga, m4a, wav, webm
                // For MP3, "audio/mpeg" or "audio/mp3". For M4A, "audio/m4a" or "audio/x-m4a".
                // Let's assume Path.GetExtension helps, or be explicit if chunks are always MP3.
                string mimeType = Path.GetExtension(filePath).ToLowerInvariant() switch {
                    ".mp3" => "audio/mpeg",
                    ".m4a" => "audio/mp4", // m4a often uses mp4 container
                    ".wav" => "audio/wav",
                    ".webm" => "audio/webm",
                    _ => "application/octet-stream" // Fallback
                };
                streamContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                content.Add(streamContent, "file", Path.GetFileName(filePath));
                
                content.Add(new StringContent("whisper-1"), "model");
                content.Add(new StringContent("verbose_json"), "response_format"); // Crucial for timestamps
                // content.Add(new StringContent("segment"), "timestamp_granularities[]"); // For word timestamps as part of segments in verbose_json
                // content.Add(new StringContent("word"), "timestamp_granularities[]");

                if (!string.IsNullOrWhiteSpace(languageCode))
                {
                    content.Add(new StringContent(languageCode), "language");
                }
                // Add prompt if needed: content.Add(new StringContent("TCU, Plenário, Câmara"), "prompt");

                HttpResponseMessage response = await client.PostAsync(OpenAiTranscriptionEndpoint, content);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("{LogPrefix}: OpenAI API request failed for Event {EventId}, File {FilePath}. Status: {StatusCode}. Response: {ErrorBody}",
                        logPrefix, liveEventIdForLogging, filePath, response.StatusCode, responseBody);
                    return null;
                }
                
                _logger.LogDebug("{LogPrefix}: OpenAI API successful response for Event {EventId}, File {FilePath}.", logPrefix, liveEventIdForLogging, filePath);
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var transcription = JsonSerializer.Deserialize<TranscriptionResult>(responseBody, options);
                
                _logger.LogInformation("{LogPrefix}: Successfully transcribed audio for Event {EventId}, File {FilePath}. Duration: {Duration}s, Language: {Language}", 
                    logPrefix, liveEventIdForLogging, filePath, transcription?.Duration, transcription?.Language);
                return transcription;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{LogPrefix}: Exception during OpenAI audio transcription for Event {EventId}, File: {FilePath}", 
                    logPrefix, liveEventIdForLogging, filePath);
                return null;
            }
        }

        private void TryDeleteFile(string filePath, string fileDescription = "temporary file")
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Cleaned up {FileDescription}: {FilePath}", fileDescription, filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up {FileDescription}: {FilePath}", fileDescription, filePath);
            }
        }

        private void TryDeleteDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return;
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true); 
                    _logger.LogDebug("Cleaned up temporary directory: {DirectoryPath}", directoryPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary directory: {DirectoryPath}", directoryPath);
            }
        }
    }
}