using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ImageConversionApi
{
    [ApiController]
    public class ImageConversionController : ControllerBase
    {
        private readonly ILogger<ImageConversionController> _logger;
        private readonly GhostScriptConfig _config;

        public ImageConversionController(ILogger<ImageConversionController> logger, GhostScriptConfig config)
        {
            _logger = logger;
            _config = config;
        }

        /// <summary>
        /// Takes a pdf and returns a zip file containing a jpeg for each page of the PDF.
        /// </summary>
        /// <returns></returns>
        [HttpPost("image/tiff-to-pdf")]
        [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = int.MaxValue)]
        [DisableRequestSizeLimit]
        public IActionResult ConvertPdfToJpegs(IFormFile file)
        {
            var guid = Guid.NewGuid();
            var tempFolderPath = Path.Combine(_config.TempImageFolder, guid.ToString());
            try
            {
                if ((file?.Length ?? 0) == 0)
                {
                    return BadRequest("No file was uploaded or the file had no content.");
                }

                var inputFilePath = Path.Combine(tempFolderPath, "input");


                var jpegPaths = ConvertTiffToJpegs(file, inputFilePath);

                // if (!IsVirusClean(inputFilePath))
                // {
                //     return BadRequest("Virus detected in uploaded file.");
                // }

                var outputFolder = Path.Combine(tempFolderPath, "output");
                var outputFilePath = Path.Combine(outputFolder, Path.ChangeExtension(file.FileName, "pdf"));

                var cmd =
                    $"-dNOPAUSE -q -sDEVICE=pdfwrite -r500 -dBATCH -sOutputFile=\"{outputFilePath}\" \"{_config.JpegImageConverterPath}\" -c \"{BuildPdfFromJpegPageCommand(jpegPaths)}\"";

                var myProcess = new Process
                {
                    StartInfo =
                    {
                        FileName = _config.GhostScriptExePath, Arguments = cmd, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardError = true
                    }
                };

                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }
                myProcess.Start();
                var errorMessage = myProcess.StandardError.ReadToEnd();

                myProcess.WaitForExit();

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, errorMessage);
                }

                var outputFileBytes = System.IO.File.ReadAllBytes(outputFilePath);
                var outputStream = new MemoryStream(outputFileBytes);



                return File(outputStream, "application/pdf", Path.GetFileName(outputFilePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
            finally
            {
                if (Directory.Exists(tempFolderPath))
                {
                    Directory.Delete(tempFolderPath, true);
                }
            }
        }

        private string BuildPdfFromJpegPageCommand(IEnumerable<string> inputFileNames)
        {
            if (inputFileNames?.Any() != true) { throw new ArgumentException($"The parameter {nameof(inputFileNames)} must not be null or empty.", nameof(inputFileNames)); }
            var stringBuilder = new StringBuilder();
            foreach (var inputFileName in inputFileNames ?? Enumerable.Empty<string>())
            {
                stringBuilder.Append($"({inputFileName}) viewJPEG showpage ");
            }

            return stringBuilder.ToString().Replace(@"\", @"//"); //Change the file separators to work inside the ghostscript arguments.
        }

        /// <summary>
        /// Converts a tiff to jpegs, returns the file names of the created files.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="inputFilePath"></param>
        /// <returns></returns>
        private static List<string> ConvertTiffToJpegs(IFormFile file, string inputFilePath)
        {
            if (!Directory.Exists(inputFilePath))
            {
                Directory.CreateDirectory(inputFilePath);
            }
            var jpegPaths = new List<string>();
            using (var image = Image.FromStream(file.OpenReadStream()))
            {
                var frameDimension = new FrameDimension(image.FrameDimensionsList[0]);
                var pageCount = image.GetFrameCount(frameDimension);
                for (var pageNumber = 0; pageNumber < pageCount; pageNumber++)
                {
                    image.SelectActiveFrame(frameDimension, pageNumber);
                    using var bmp = new Bitmap(image);
                    var jpegPath = Path.Combine(inputFilePath, Path.GetFileNameWithoutExtension(file.FileName), $"{pageNumber}.jpg");
                    jpegPaths.Add(jpegPath);
                    bmp.Save(jpegPath, ImageFormat.Jpeg);
                }
            }

            return jpegPaths;
        }


        [HttpPost("image/pdf-to-tiff")]
        public IActionResult ConvertPdfToTiff(IFormFile file)
        {
            var inputFilePath = string.Empty;
            var outputFilePath = string.Empty;
            try
            {
                if ((file?.Length ?? 0) == 0)
                {
                    //TODO:  Scan the file for viruses, make sure it is a PDF.
                    return BadRequest("No file was uploaded or the file had no content.");
                }

                var scanFileStream = new MemoryStream();
                file.CopyTo(scanFileStream);

                if (IsPdf(file.FileName, scanFileStream))
                {
                    return BadRequest("You must upload a pdf file.");
                }

                var guid = Guid.NewGuid();

                inputFilePath = Path.Combine(_config.TempImageFolder, $"{guid}.pdf");

                using var inputFileStream = new FileStream(inputFilePath, FileMode.Create);
                file.CopyTo(inputFileStream);

                // if (!IsVirusClean(inputFilePath))
                // {
                //     return BadRequest("Virus detected in uploaded file.");
                // }

                outputFilePath = Path.Combine(_config.TempImageFolder, $"{guid}.tiff");

                var cmd =
                    $"-dNOPAUSE -q -sDEVICE=tiff24nc -r500 -dBATCH -sOutputFile=\"{outputFilePath}\" \"{inputFilePath}\"";

                var myProcess = new Process
                {
                    StartInfo =
                    {
                        FileName = _config.GhostScriptExePath, Arguments = cmd, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardError = true
                    }
                };


                myProcess.Start();
                var errorMessage = myProcess.StandardError.ReadToEnd();

                myProcess.WaitForExit();

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, errorMessage);
                }

                var outputFileBytes = System.IO.File.ReadAllBytes(outputFilePath);
                var outputStream = new MemoryStream(outputFileBytes);



                return File(outputStream, "image/tiff");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
            finally
            {
                if (System.IO.File.Exists(inputFilePath))
                {
                    System.IO.File.Delete(inputFilePath);
                }
                if (System.IO.File.Exists(outputFilePath))
                {
                    System.IO.File.Delete(outputFilePath);
                }
            }
        }

        private static bool IsPdf(string fileName, string path)
        {
            using var f = System.IO.File.OpenRead(path);
            return IsPdf(fileName, f);
        }

        private static bool IsPdf(string fileName, Stream stream)
        {
            if (!fileName.EndsWith(".pdf"))
            {
                return false;
            }
            var pdfString = "%PDF-";
            var pdfBytes = Encoding.ASCII.GetBytes(pdfString);
            var len = pdfBytes.Length;
            var buf = new byte[len];
            var remaining = len;
            var pos = 0;

            while (remaining > 0)
            {
                var amtRead = stream.Read(buf, pos, remaining);
                if (amtRead == 0) return false;
                remaining -= amtRead;
                pos += amtRead;
            }

            return pdfBytes.SequenceEqual(buf);
        }

        private static bool IsVirusClean(string filePath)
        {
            // if (!AppManager.AppSettings.DoVirusScan) return true;
            var isClean = false;
            var filePathReport = filePath + ".report";
            var myProcess = new Process();
            myProcess.StartInfo.FileName = "myPathToVirusScanner";//AppManager.AppSettings.VirusScanPath;
            //myProcess.StartInfo.Arguments = $"/SCAN=\"{filePath}\" /REPORT=\"{filePathReport}\"";
            myProcess.StartInfo.Arguments = $"\"{filePath}\" /p=1 /r=\"{filePathReport}\"";
            myProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            myProcess.Start();
            myProcess.WaitForExit();
            myProcess.Dispose();

            //add some time for report to be written to file
            for (var i = 0; i < 100; i++)
            {
                if (System.IO.File.Exists(filePathReport))
                    break;
                else
                    Thread.Sleep(100);
            }

            using (var streamReader = new StreamReader(filePathReport))
            {
                var fileContents = streamReader.ReadToEnd();
                isClean = fileContents.Contains("Infected files: 0");

                if (!isClean)
                    System.IO.File.Delete(filePath);
            }

            System.IO.File.Delete(filePathReport);

            return isClean;
        }
    }
}
