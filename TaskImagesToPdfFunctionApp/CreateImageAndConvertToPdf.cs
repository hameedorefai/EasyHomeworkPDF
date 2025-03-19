using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using System.Drawing.Drawing2D;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace TaskImagesToPdfFunctionApp
{
    internal class CreateImageAndConvertToPdf
    {
        private readonly string blobConnectionString;
        private readonly string containerName;

        public CreateImageAndConvertToPdf()
        {
            blobConnectionString = Environment.GetEnvironmentVariable("BlobConnectionString", EnvironmentVariableTarget.Process);
            containerName = Environment.GetEnvironmentVariable("ContainerName", EnvironmentVariableTarget.Process);
        }

        [Function("CreateImageAndConvertToPdf")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            try
            {
                // Extract query parameters
                var studentName = req.Query["studentName"];
                var studentId = req.Query["studentId"];
                var subjectName = req.Query["subjectName"];
                var subjectCode = req.Query["subjectCode"];
                var instructorName = req.Query["instructorName"];
                var sectionNumber = req.Query["sectionNumber"];

                studentName = string.IsNullOrEmpty(studentName) ? "" : studentName;
                studentId = string.IsNullOrEmpty(studentId) ? "" : studentId;
                subjectName = string.IsNullOrEmpty(subjectName) ? "" : subjectName;
                subjectCode = string.IsNullOrEmpty(subjectCode) ? "" : subjectCode;
                instructorName = string.IsNullOrEmpty(instructorName) ? "" : instructorName;
                sectionNumber = string.IsNullOrEmpty(sectionNumber) ? "" : sectionNumber;

                subjectName = ReformatTextWithParentheses(subjectName);
                // Check if there are any files in the request
                var formFiles = req.Form.Files;
                if (formFiles.Count == 0)
                {
                    return new BadRequestObjectResult("No image files uploaded.");
                }

                // Create a new PDF document
                var pdfDocument = new PdfDocument();
                
                // Process first image with details
                var firstImage = CreateTheFirstPageImageWithDetails( studentName,  studentId,  subjectName,  subjectCode,  instructorName, sectionNumber);
                var firstImageStream = new MemoryStream();
                firstImage.Save(firstImageStream, ImageFormat.Png);
                firstImageStream.Position = 0;

                var xImage = XImage.FromStream(() => firstImageStream);
                AddImageToPdf(pdfDocument, xImage);

                // Process additional uploaded images
                foreach (var file in formFiles)
                {
                    using (var stream = file.OpenReadStream())
                    {
                        using (var uploadedImage = new Bitmap(stream))
                        {
                            // Convert image to PDF and add
                            using (var memoryStream = new MemoryStream())
                            {
                                uploadedImage.Save(memoryStream, ImageFormat.Png);
                                memoryStream.Position = 0;

                                var image = XImage.FromStream(() => memoryStream);
                                AddImageToPdf(pdfDocument, image);
                            }
                        }
                    }
                }

                // Save PDF to memory stream
                var pdfStream = new MemoryStream();
                try
                {
                    pdfDocument.Save(pdfStream);
                    pdfStream.Position = 0;

                    studentName = string.IsNullOrEmpty(studentName) ? "" : $"_الطالب {studentName}";

                    //return new FileStreamResult(pdfStream, "application/pdf")
                    //{
                    //    FileDownloadName = $"حل_نشاط_{subjectName}{studentName}.pdf"
                    //};

                    string uniqueId = Guid.NewGuid().ToString("N"); // Removes dashes

                    // upload to Azure Blob Storage
                    string fileName = $"حل نشاط {subjectName}{studentName}_{uniqueId}.pdf";
                    string fileUrl = await UploadToAzureBlob(pdfStream, fileName);

                    // returns uploaded file url.
                    return new OkObjectResult(new { FileUrl = fileUrl });

                }
                catch (Exception ex)
                {
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                }
            }
            catch (Exception ex)
            {
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }


        private async Task<string> UploadToAzureBlob(Stream fileStream, string fileName)
        {
            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);
           
            var blobClient = containerClient.GetBlobClient(fileName);
            await blobClient.UploadAsync(fileStream, overwrite: true);

            return blobClient.Uri.ToString();
        }

        private Bitmap CreateTheFirstPageImageWithDetails(string studentName, string studentId, string subjectName, string subjectCode, string instructorName, string sectionNumber)
        {
            var width = 1190;  // عرض A4 بدقة 300 dpi
            var height = 1684; // ارتفاع A4 بدقة 300 dpi

            var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                var mainFont = new Font("Arial", 24, FontStyle.Bold);  // خط رئيسي بحجم مناسب
                var subFont = new Font("Arial", 20, FontStyle.Regular); // خط فرعي بحجم أقل
                var centerFont = new Font("Arial", 28, FontStyle.Bold);  // خط كبير للعناوين الرئيسية

                var brush = Brushes.Black;
                var centerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var rightFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                var leftFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

                graphics.DrawString("بسم الله الرحمن الرحيم", centerFont, brush, new RectangleF(0, 50, width, 50), centerFormat);

                try
                {
                    var logoUrl = "https://www.zamayl.com/assets/img/site/qouLogoNew.png";
                    using (var client = new System.Net.WebClient())
                    {
                        var logoBytes = client.DownloadData(logoUrl);
                        using (var memoryStream = new System.IO.MemoryStream(logoBytes))
                        {
                            var logoImage = Image.FromStream(memoryStream);
                            var logoWidth = 120; // حجم الشعار
                            var logoHeight = 120;
                            var logoX = (width - logoWidth) / 2;
                            var logoY = 120;

                            graphics.DrawImage(logoImage, new Rectangle(logoX, logoY, logoWidth, logoHeight));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error loading logo: " + ex.Message);
                }

                // تفاصيل العناوين
                graphics.DrawString("جامعة القدس المفتوحة", centerFont, brush, new RectangleF(0, 260, width, 50), centerFormat);
                graphics.DrawString("حل النشاط للفصل الدراسي الثاني 1242", subFont, Brushes.Red, new RectangleF(0, 310, width, 50), centerFormat);
                graphics.DrawString("2024/2025", subFont, brush, new RectangleF(0, 360, width, 50), centerFormat);

                // تفاصيل الطالب والمقرر
                var detailsStartY = 440;
                var detailsHeight = 40; // ارتفاع كل سطر

                // تحديد نسبة العرض لكل عمود
                float leftColumnWidth = 0.39f * width; // العمود الأيسر (39%)
                float rightColumnWidth = 0.61f * width; // العمود الأيمن (61%)

                // إعداد تنسيق العمود الأيسر (محاذاة إلى اليمين)
                StringFormat leftColumnRightAlignedFormat = new StringFormat
                {
                    Alignment = StringAlignment.Far, // محاذاة إلى اليمين
                    LineAlignment = StringAlignment.Center // محاذاة عمودية وسط
                };

                // العمود الأيمن (61% من العرض)
                graphics.DrawString($"اسم الطالب: {studentName}", mainFont, brush, new RectangleF(leftColumnWidth, detailsStartY, rightColumnWidth - 50, detailsHeight), rightFormat);
                graphics.DrawString($"اسم المقرر: {subjectName}", mainFont, brush, new RectangleF(leftColumnWidth, detailsStartY + detailsHeight, rightColumnWidth - 50, detailsHeight), rightFormat);
                graphics.DrawString($"عضو هيئة التدريس: {instructorName}", mainFont, brush, new RectangleF(leftColumnWidth, detailsStartY + detailsHeight * 2, rightColumnWidth - 50, detailsHeight), rightFormat);

                // العمود الأيسر (39% من العرض، النص يبدأ من اليمين)
                graphics.DrawString($"الرقم الجامعي: {studentId}", mainFont, brush, new RectangleF(50, detailsStartY, leftColumnWidth - 50, detailsHeight), leftColumnRightAlignedFormat);
                graphics.DrawString($"رقم المقرر: {subjectCode}", mainFont, brush, new RectangleF(50, detailsStartY + detailsHeight, leftColumnWidth - 50, detailsHeight), leftColumnRightAlignedFormat);
                graphics.DrawString($"رقم الشعبة: {sectionNumber}", mainFont, brush, new RectangleF(50, detailsStartY + detailsHeight * 2, leftColumnWidth - 50, detailsHeight), leftColumnRightAlignedFormat);
                // إضافة الشعار كعلامة مائية في الأسفل
                try
                {
                    var logoUrl = "https://www.zamayl.com/assets/img/site/zamayl-task-service-logo.png";
                    using (var client = new System.Net.WebClient())
                    {
                        var logoBytes = client.DownloadData(logoUrl);
                        using (var memoryStream = new System.IO.MemoryStream(logoBytes))
                        {
                            var logoImage = Image.FromStream(memoryStream);

                            // حجم الشعار الكبير (كنمط علامة مائية)
                            var watermarkWidth = 400; // حجم الشعار
                            var watermarkHeight = 400;
                            var watermarkX = (width - watermarkWidth) / 2; // محاذاة للشعار في المنتصف
                            var watermarkY = height - watermarkHeight - 50; // وضعه أسفل الصفحة

                            // إنشاء ImageAttributes مع ColorMatrix لتعديل الشفافية
                            var imageAttributes = new ImageAttributes();
                            var colorMatrix = new System.Drawing.Imaging.ColorMatrix();
                            colorMatrix.Matrix33 = 1f; // تعديل الشفافية بنسبة 20%

                            imageAttributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                            // رسم الشعار مع الشفافية
                            graphics.DrawImage(logoImage, new Rectangle(watermarkX, watermarkY, watermarkWidth, watermarkHeight), 0, 0, logoImage.Width, logoImage.Height, GraphicsUnit.Pixel, imageAttributes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error loading watermark logo: " + ex.Message);
                }
            }

            return bitmap;
        }




        public string ReformatTextWithParentheses(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            return input.Replace("(", "").Replace(")", "");
        }




        private void AddImageToPdf(PdfDocument pdfDocument, XImage image)
        {
            double a4Width = 595.28; // A4 width in points
            double a4Height = 841.89; // A4 height in points

            double imageRatio = (double)image.PixelWidth / image.PixelHeight;
            double scaledWidth, scaledHeight;

            if (imageRatio > a4Width / a4Height)
            {
                scaledWidth = a4Width;
                scaledHeight = a4Width / imageRatio;
            }
            else
            {
                scaledHeight = a4Height;
                scaledWidth = a4Height * imageRatio;
            }

            double offsetX = (a4Width - scaledWidth) / 2;
            double offsetY = (a4Height - scaledHeight) / 2;

            var pdfPage = pdfDocument.AddPage();
            using (var gfx = XGraphics.FromPdfPage(pdfPage))
            {
                gfx.DrawImage(image, offsetX, offsetY, scaledWidth, scaledHeight);
            }
        }
    }
}





