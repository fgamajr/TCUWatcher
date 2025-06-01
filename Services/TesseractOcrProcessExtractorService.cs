using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TCUWatcher.API.Utils; // For TCUProcessValidator
using Tesseract;             // For Charles Weld's Tesseract wrapper
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing; // For Mutate, Crop

namespace TCUWatcher.API.Services
{
    public class TesseractOcrProcessExtractorService : IOcrProcessExtractorService
    {
        private readonly ILogger<TesseractOcrProcessExtractorService> _logger;
        private readonly string _tessDataPath; // Path to the parent of the 'tessdata' directory, or where TESSDATA_PREFIX points
        private readonly string _ocrLanguage;  // e.g., "por"
        private readonly float _cropBottomPercentage;
        private readonly EngineMode _engineMode;
        private readonly PageSegMode _pageSegMode;

        public TesseractOcrProcessExtractorService(IConfiguration configuration, ILogger<TesseractOcrProcessExtractorService> logger)
        {
            _logger = logger;
            
            // Configure native library loading BEFORE any Tesseract operations
            ConfigureNativeLibraries(_logger);
            
            // For Charles Weld's wrapper, TESSDATA_PREFIX env var is often used.
            // Or, the 'dataPath' for new TesseractEngine(dataPath, language, mode) is the parent of the 'tessdata' directory.
            // If OcrService:TessDataPath in appsettings.json points to "./tessdata" (relative to app output), 
            // then AppContext.BaseDirectory is the parent dataPath.
            string configuredTessDataFolder = configuration["OcrService:TessDataPath"] ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
            if (Path.GetFileName(configuredTessDataFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals("tessdata", StringComparison.OrdinalIgnoreCase))
            {
                _tessDataPath = Path.GetDirectoryName(configuredTessDataFolder) ?? AppContext.BaseDirectory;
            }
            else 
            {
                _tessDataPath = configuredTessDataFolder; // Assume it's already the parent path or Tesseract will find it
            }

            _ocrLanguage = configuration["OcrService:Language"] ?? "por";
            _cropBottomPercentage = configuration.GetValue<float?>("OcrService:CropBottomPercentage") ?? 0.25f;
            _engineMode = EngineMode.Default; // Tesseract.EngineMode
            _pageSegMode = PageSegMode.AutoOsd; // Tesseract.PageSegMode

            _logger.LogInformation("TesseractOcrService (Charles Weld wrapper) initialized. TessData Parent Path: '{TessDataPath}', Language: '{Lang}', CropBottomPercent: {CropPercent}",
                _tessDataPath, _ocrLanguage, _cropBottomPercentage);

            // Verify language data file exists where TesseractEngine would look
            string expectedTrainedDataFile = Path.Combine(_tessDataPath, "tessdata", $"{_ocrLanguage}.traineddata");
            if (!File.Exists(expectedTrainedDataFile))
            {
                _logger.LogCritical("Tesseract language data file '{Lang}.traineddata' not found in expected path '{ExpectedPath}'. OCR will likely fail. Ensure TESSDATA_PREFIX is set or '{TessDataPath}/tessdata' contains the file.", 
                                    _ocrLanguage, expectedTrainedDataFile, _tessDataPath);
            }
        }

        private static void ConfigureNativeLibraries(ILogger logger) // Pass logger here
        {
            logger.LogInformation("Setting DllImportResolver for Tesseract assembly...");
            NativeLibrary.SetDllImportResolver(typeof(Tesseract.TesseractEngine).Assembly, (libraryName, assembly, searchPath) =>
            {
                logger.LogDebug("DllImportResolver: Attempting to resolve '{LibraryName}'.", libraryName);
                if (libraryName == "libleptonica-1.82.0.so")
                {
                    string pathToLoad = "/usr/lib/libleptonica.so.6.0.0"; // Or /usr/lib/libleptonica.so
                    logger.LogInformation("DllImportResolver: Matched 'libleptonica-1.82.0.so'. Attempting to load '{PathToLoad}'.", pathToLoad);
                    try
                    {
                        IntPtr handle = NativeLibrary.Load(pathToLoad);
                        if (handle != IntPtr.Zero)
                        {
                            logger.LogInformation("DllImportResolver: Successfully loaded '{PathToLoad}' for 'libleptonica-1.82.0.so'. Handle: {Handle}", pathToLoad, handle);
                        }
                        else
                        {
                            logger.LogError("DllImportResolver: NativeLibrary.Load for '{PathToLoad}' returned IntPtr.Zero.", pathToLoad);
                        }
                        return handle;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "DllImportResolver: Exception during NativeLibrary.Load for '{PathToLoad}'.", pathToLoad);
                        return IntPtr.Zero;
                    }
                }
                logger.LogDebug("DllImportResolver: Library '{LibraryName}' not handled by this resolver.", libraryName);
                return IntPtr.Zero;
            });
        }

        public async Task<List<string>> ExtractValidatedProcessNumbersAsync(string imagePath, string liveEventIdForLogging)
        {
            var validatedProcessNumbers = new List<string>();
            if (!File.Exists(imagePath))
            {
                _logger.LogWarning("OCR: Image file not found at {ImagePath} for event {EventId}", imagePath, liveEventIdForLogging);
                return validatedProcessNumbers;
            }

            _logger.LogDebug("OCR (Charles Weld Tesseract): Processing image {ImagePath} for event {EventId}", imagePath, liveEventIdForLogging);
            byte[]? croppedImageBytes = null;

            try
            {
                using (var image = await SixLabors.ImageSharp.Image.LoadAsync(imagePath))
                {
                    int originalWidth = image.Width;
                    int originalHeight = image.Height;

                    int roiHeight = (int)(originalHeight * _cropBottomPercentage);
                    if (roiHeight <= 0 || roiHeight > originalHeight) 
                    {
                        _logger.LogWarning("Calculated ROI height {CalculatedRoiHeight} is invalid for image height {OriginalHeight}. Using full image height for crop.", roiHeight, originalHeight);
                        roiHeight = originalHeight;
                    }
                    int roiY = originalHeight - roiHeight;
                    
                    var cropRectangle = new SixLabors.ImageSharp.Rectangle(0, roiY, originalWidth, roiHeight);
                    _logger.LogDebug("OCR: Cropping image {ImagePath} to ROI x:{X}, y:{Y}, w:{Width}, h:{Height}",
                        imagePath, cropRectangle.X, cropRectangle.Y, cropRectangle.Width, cropRectangle.Height);

                    image.Mutate(x => x.Crop(cropRectangle));
                    
                    using (var ms = new MemoryStream())
                    {
                        await image.SaveAsPngAsync(ms); 
                        croppedImageBytes = ms.ToArray();
                    }
                }

                if (croppedImageBytes == null || croppedImageBytes.Length == 0)
                {
                    _logger.LogWarning("OCR: Cropped image data is empty for {ImagePath}.", imagePath);
                    return validatedProcessNumbers;
                }

                return await Task.Run(() =>
                {
                    var currentValidatedNumbers = new List<string>();
                    try
                    {
                        using (var engine = new TesseractEngine(_tessDataPath, _ocrLanguage, _engineMode))
                        {
                            _logger.LogWarning("Instanciando TesseractEngine em: " + _tessDataPath + ", arquivos em: " + Path.Combine(_tessDataPath, $"{_ocrLanguage}.traineddata"));
                            _logger.LogDebug("OCR: Processing cropped image from {ImagePath} for event {EventId}", imagePath, liveEventIdForLogging);
                            engine.DefaultPageSegMode = _pageSegMode;
                            using (var pix = Pix.LoadFromMemory(croppedImageBytes))
                            {
                                using (var page = engine.Process(pix)) // Process the entire (pre-cropped) Pix object
                                {
                                    string text = page.GetText() ?? string.Empty;
                                    _logger.LogDebug("OCR Raw Text from cropped {ImagePath} for event {EventId}:\n---\n{Text}\n---", imagePath, liveEventIdForLogging, text);

                                    var regex = new Regex(@"(\d{3}\.?\d{3}/\d{4}-\d)", RegexOptions.IgnoreCase);
                                    var matches = regex.Matches(text);

                                    if (matches.Count > 0)
                                    {
                                        _logger.LogDebug("Found {Count} potential process numbers in OCR text.", matches.Count);
                                    }

                                    foreach (System.Text.RegularExpressions.Match match in matches)
                                    {
                                        string potentialProcess = match.Value.Replace(",", ".");
                                        if (TCUProcessValidator.VerifyDV(potentialProcess))
                                        {
                                            string formattedProcess = $"TC {potentialProcess}";
                                            if (!currentValidatedNumbers.Contains(formattedProcess))
                                            {
                                                currentValidatedNumbers.Add(formattedProcess);
                                                _logger.LogInformation("OCR: Validated process number '{ProcessNumber}' found for event {EventId}",
                                                    formattedProcess, liveEventIdForLogging);
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogDebug("OCR: Candidate '{ProcessNumber}' failed DV check.", potentialProcess);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (TesseractException tessEx)
                    {
                        _logger.LogError(tessEx, "OCR: TesseractException processing cropped image from {ImagePath} for event {EventId}. Message: {TessExMessage}", imagePath, liveEventIdForLogging, tessEx.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "OCR: Generic error during Tesseract Task.Run for cropped image from {ImagePath}, event {EventId}.", imagePath, liveEventIdForLogging);
                    }
                    return currentValidatedNumbers;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR: Error during image loading/cropping for {ImagePath}, event {EventId}.", imagePath, liveEventIdForLogging);
                return validatedProcessNumbers; 
            }
        }
    }
}