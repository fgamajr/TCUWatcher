using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography; // Required for SHA256
using System.Threading.Tasks;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;

namespace TCUWatcher.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UploadsController : ControllerBase
    {
        private readonly IMongoService _mongoService;
        private readonly ILogger<UploadsController> _logger;
        private readonly string _uploadStagingPath; 

        public UploadsController(IMongoService mongoService, ILogger<UploadsController> logger, IConfiguration configuration)
        {
            _mongoService = mongoService;
            _logger = logger;
            _uploadStagingPath = configuration["Storage:UploadStagingPath"] ?? Path.Combine(AppContext.BaseDirectory, "uploaded_videos_staging");
            
            try
            {
                if (!Directory.Exists(_uploadStagingPath))
                {
                    Directory.CreateDirectory(_uploadStagingPath);
                    _logger.LogInformation("Created upload staging directory: {Path}", _uploadStagingPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to create or access upload staging directory: {Path}. File uploads may fail.", _uploadStagingPath);
            }
        }

        [HttpPost("video")]
        [RequestSizeLimit(10L * 1024 * 1024 * 1024)] // Example: 10 GB limit
        [RequestFormLimits(MultipartBodyLengthLimit = 10L * 1024 * 1024 * 1024)] 
        public async Task<IActionResult> UploadVideo(
            [FromForm] IFormFile videoFile, 
            [FromForm] string title, 
            [FromForm] string videoId)
        {
            if (videoFile == null || videoFile.Length == 0)
                return BadRequest(new { message = "No video file uploaded or file is empty." });
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { message = "Title is required." });
            if (string.IsNullOrWhiteSpace(videoId)) 
                return BadRequest(new { message = "VideoId is required." });

            var originalFileName = Path.GetFileName(videoFile.FileName);
            var uniqueFileNameInStaging = Guid.NewGuid().ToString() + Path.GetExtension(originalFileName);
            var filePathOnServer = Path.Combine(_uploadStagingPath, uniqueFileNameInStaging);

            try
            {
                _logger.LogInformation("Receiving video file '{OriginalFileName}' for VideoId '{VideoId}', Title '{Title}'. Staging to '{FilePathOnServer}'.",
                    originalFileName, videoId, title, filePathOnServer);

                await using (var stream = new FileStream(filePathOnServer, FileMode.Create))
                {
                    await videoFile.CopyToAsync(stream);
                }
                _logger.LogInformation("Video file staged successfully to '{FilePathOnServer}'.", filePathOnServer);

                string fileHash;
                try
                {
                    _logger.LogDebug("Calculating SHA256 hash for '{FilePathOnServer}'...", filePathOnServer);
                    await using (var stream = System.IO.File.OpenRead(filePathOnServer))
                    using (var sha256 = SHA256.Create())
                    {
                        byte[] hashBytes = await sha256.ComputeHashAsync(stream);
                        fileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    }
                    _logger.LogInformation("Calculated SHA256 hash for '{OriginalFileName}': {FileHash}", originalFileName, fileHash);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to calculate hash for uploaded file '{OriginalFileName}' (staged at {FilePathOnServer}).", originalFileName, filePathOnServer);
                    TryDeleteStagedFile(filePathOnServer, "hash calculation failure");
                    return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error calculating file hash." });
                }

                var existingEventWithHash = await _mongoService.GetLiveEventByFileHashAsync(fileHash); 

                if (existingEventWithHash != null)
                {
                    _logger.LogInformation("Uploaded file '{OriginalFileName}' with hash {FileHash} is a duplicate of existing LiveEventId: {ExistingEventId} (VideoId: {ExistingVideoId}).",
                        originalFileName, fileHash, existingEventWithHash.Id, existingEventWithHash.VideoId);
                    
                    TryDeleteStagedFile(filePathOnServer, "duplicate file");

                    return Conflict(new { 
                        message = "This video file has already been uploaded and processed (or is pending processing).", 
                        duplicateOfEventId = existingEventWithHash.Id,
                        duplicateOfVideoId = existingEventWithHash.VideoId,
                        processingStatus = existingEventWithHash.Status?.ToString()
                    });
                }

                var liveEvent = new LiveEvent
                {
                    Title = title,
                    VideoId = videoId, 
                    StartedAt = DateTime.UtcNow, 
                    IsManualUpload = true,
                    Status = ProcessingStatus.Pending, 
                    LocalFilePath = filePathOnServer,  
                    IsLive = false, 
                    FileHash = fileHash,               
                    UploadedAt = DateTime.UtcNow     
                };

                await _mongoService.CreateLiveEventAsync(liveEvent);
                _logger.LogInformation("LiveEvent document created for manual upload: {LiveEventIdDb}, VideoId: {VideoIdProvided}, Hash: {FileHash}", 
                    liveEvent.Id, videoId, fileHash);

                return Accepted(new { message = "Video uploaded successfully and queued for processing.", eventId = liveEvent.Id, stagedFilePath = filePathOnServer });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during video upload process for VideoId '{VideoId}'.", videoId);
                TryDeleteStagedFile(filePathOnServer, "general upload error");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An unexpected error occurred during file upload." });
            }
        }

        private void TryDeleteStagedFile(string filePath, string reasonForDeletion)
        {
            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                    _logger.LogInformation("Cleaned up staged file '{FilePath}' due to {Reason}.", filePath, reasonForDeletion);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete staged file '{FilePath}' after {Reason}.", filePath, reasonForDeletion);
            }
        }
    }
}