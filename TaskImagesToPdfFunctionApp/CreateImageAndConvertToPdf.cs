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
        private readonly ILogger<CreateImageAndConvertToPdf> logger;

        // الشعارات ثابتة، فنجلبها مرة واحدة لكل نسخة بدل كل طلب
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> logoCache = new();

        public CreateImageAndConvertToPdf(ILogger<CreateImageAndConvertToPdf> logger)
        {
            this.logger = logger;
            blobConnectionString = Environment.GetEnvironmentVariable("BlobConnectionString", EnvironmentVariableTarget.Process);
            containerName = Environment.GetEnvironmentVariable("ContainerName", EnvironmentVariableTarget.Process);
        }

        [Function("CreateImageAndConvertToPdf")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
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
                var semesterCode = req.Query["semesterCode"].ToString();

                studentName = string.IsNullOrEmpty(studentName) ? "" : studentName;
                studentId = string.IsNullOrEmpty(studentId) ? "" : studentId;
                subjectName = string.IsNullOrEmpty(subjectName) ? "" : subjectName;
                subjectCode = string.IsNullOrEmpty(subjectCode) ? "" : subjectCode;
                instructorName = string.IsNullOrEmpty(instructorName) ? "" : instructorName;
                sectionNumber = string.IsNullOrEmpty(sectionNumber) ? "" : sectionNumber;

                subjectName = ReformatTextWithParentheses(subjectName);

                if (string.IsNullOrWhiteSpace(blobConnectionString) || string.IsNullOrWhiteSpace(containerName))
                {
                    logger.LogError("BlobConnectionString/ContainerName app settings are missing.");
                    return new ObjectResult("Storage is not configured.") { StatusCode = StatusCodes.Status500InternalServerError };
                }

                // Check if there are any files in the request
                if (!req.HasFormContentType)
                {
                    return new BadRequestObjectResult("Request must be multipart/form-data.");
                }

                var formFiles = req.Form.Files;
                if (formFiles.Count == 0)
                {
                    return new BadRequestObjectResult("No image files uploaded.");
                }

                // الفصل الدراسي يُجلب من التقويم الأكاديمي، ويمكن تجاوزه بـ semesterCode للتسليم المتأخر.
                var semester = SemesterInfo.IsValidCode(semesterCode)
                    ? SemesterInfo.FromCode(semesterCode)
                    : await CurrentSemesterProvider.GetAsync(logger, req.HttpContext.RequestAborted);

                // Create a new PDF document
                var pdfDocument = new PdfDocument();

                // Process first image with details
                var firstImageStream = new MemoryStream();
                using (var firstImage = CreateTheFirstPageImageWithDetails(studentName, studentId, subjectName, subjectCode, instructorName, sectionNumber, semester))
                {
                    firstImage.Save(firstImageStream, ImageFormat.Png);
                }
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
                    logger.LogError(ex, "Failed to save the PDF or upload it to blob storage.");
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build the PDF from the uploaded images.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }


        private async Task<string> UploadToAzureBlob(Stream fileStream, string fileName)
        {
            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);
           
            var blobClient = containerClient.GetBlobClient(fileName);

            // نتركه application/octet-stream عمداً: القراءة المجهولة من Blob تتم بنسخة API قديمة
            // لا تُرجع Content-Disposition، فلو ضبطنا application/pdf سيعرضه المتصفح بدل تنزيله.
            await blobClient.UploadAsync(fileStream, overwrite: true);

            // AbsoluteUri يعيد الرابط مُرمَّزاً (الاسم يحتوي عربية ومسافات)
            return blobClient.Uri.AbsoluteUri;
        }

        private Bitmap CreateTheFirstPageImageWithDetails(string studentName, string studentId, string subjectName, string subjectCode, string instructorName, string sectionNumber, SemesterInfo semester)
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

                // كل نصوص الغلاف عربية، فاتجاه الفقرة يجب أن يكون RTL.
                // بدونه يفترض GDI+ اتجاهاً LTR: السطر العربي الخالص يبدو سليماً بالمصادفة لأنه مقطع واحد،
                // لكن ما إن تظهر قيمة لاتينية (اسم مقرر مثل Structure1) حتى ينقلب السطر
                // فتصير التسمية يساراً والقيمة يميناً، عكس بقية السطور.
                const StringFormatFlags rtl = StringFormatFlags.DirectionRightToLeft;

                // ملاحظة: مع DirectionRightToLeft تنقلب دلالة المحاذاة، فـ Near تعني اليمين و Far تعني اليسار.
                var centerFormat = new StringFormat(rtl) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var rightFormat = new StringFormat(rtl) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

                graphics.DrawString("بسم الله الرحمن الرحيم", centerFont, brush, new RectangleF(0, 50, width, 50), centerFormat);

                try
                {
                    var logoBytes = GetLogo("https://www.zamayl.com/assets/img/site/qouLogoNew.png");
                    using (var memoryStream = new System.IO.MemoryStream(logoBytes))
                    using (var logoImage = Image.FromStream(memoryStream))
                    {
                        var logoWidth = 120; // حجم الشعار
                        var logoHeight = 120;
                        var logoX = (width - logoWidth) / 2;
                        var logoY = 120;

                        graphics.DrawImage(logoImage, new Rectangle(logoX, logoY, logoWidth, logoHeight));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error loading header logo.");
                }

                // تفاصيل العناوين
                graphics.DrawString("جامعة القدس المفتوحة", centerFont, brush, new RectangleF(0, 260, width, 50), centerFormat);
                graphics.DrawString(semester.HeaderLine, subFont, Brushes.Red, new RectangleF(0, 310, width, 50), centerFormat);
                graphics.DrawString(semester.AcademicYear, subFont, brush, new RectangleF(0, 360, width, 50), centerFormat);

                // تفاصيل الطالب والمقرر
                var detailsStartY = 440;
                var detailsHeight = 40; // ارتفاع كل سطر

                // تحديد نسبة العرض لكل عمود
                float leftColumnWidth = 0.39f * width; // العمود الأيسر (39%)
                float rightColumnWidth = 0.61f * width; // العمود الأيمن (61%)

                // إعداد تنسيق العمود الأيسر (محاذاة إلى اليمين)
                StringFormat leftColumnRightAlignedFormat = new StringFormat(rtl)
                {
                    Alignment = StringAlignment.Near, // محاذاة إلى اليمين (Near مع RTL)
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
                    var logoBytes = GetLogo("https://www.zamayl.com/assets/img/site/zamayl-task-service-logo.png");
                    using (var memoryStream = new System.IO.MemoryStream(logoBytes))
                    using (var logoImage = Image.FromStream(memoryStream))
                    using (var imageAttributes = new ImageAttributes())
                    {
                        // حجم الشعار الكبير (كنمط علامة مائية)
                        var watermarkWidth = 400; // حجم الشعار
                        var watermarkHeight = 400;
                        var watermarkX = (width - watermarkWidth) / 2; // محاذاة للشعار في المنتصف
                        var watermarkY = height - watermarkHeight - 50; // وضعه أسفل الصفحة

                        // إنشاء ImageAttributes مع ColorMatrix لتعديل الشفافية
                        var colorMatrix = new System.Drawing.Imaging.ColorMatrix();
                        colorMatrix.Matrix33 = 1f; // تعديل الشفافية بنسبة 20%

                        imageAttributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                        // رسم الشعار مع الشفافية
                        graphics.DrawImage(logoImage, new Rectangle(watermarkX, watermarkY, watermarkWidth, watermarkHeight), 0, 0, logoImage.Width, logoImage.Height, GraphicsUnit.Pixel, imageAttributes);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error loading watermark logo.");
                }
            }

            return bitmap;
        }




        // يجلب الشعار مرة واحدة ثم يعيده من الذاكرة في الطلبات التالية
        private static byte[] GetLogo(string logoUrl)
        {
            return logoCache.GetOrAdd(logoUrl, url => httpClient.GetByteArrayAsync(url).GetAwaiter().GetResult());
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





