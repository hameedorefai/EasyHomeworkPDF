using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace TaskImagesToPdfFunctionApp
{
    /// <summary>
    /// رفع ملفات الأنشطة إلى Cloudflare R2 عبر واجهة S3، بنفس حساب بقية خدمات زمايل.
    /// </summary>
    internal sealed class R2StorageService
    {
        private const string DefaultBucketName = "task";
        private const string DefaultPublicBaseUrl = "https://task.zamayl.com";

        // AmazonS3Client آمن للاستخدام المتزامن ومكلف الإنشاء، فننشئه مرة واحدة لكل نسخة من التطبيق.
        private static AmazonS3Client? sharedClient;
        private static readonly object clientLock = new object();

        private readonly string accountId;
        private readonly string accessKey;
        private readonly string secretKey;
        private readonly string bucketName;
        private readonly string publicBaseUrl;

        public R2StorageService()
        {
            accountId = ReadSetting("R2AccountId");
            accessKey = ReadSetting("R2AccessKey");
            secretKey = ReadSetting("R2SecretKey");
            bucketName = ReadSetting("R2BucketName", DefaultBucketName);
            publicBaseUrl = ReadSetting("R2PublicBaseUrl", DefaultPublicBaseUrl).TrimEnd('/');
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(accountId)
            && !string.IsNullOrWhiteSpace(accessKey)
            && !string.IsNullOrWhiteSpace(secretKey)
            && !string.IsNullOrWhiteSpace(bucketName);

        /// <summary>
        /// يرفع الملف ويعيد رابطه العام الدائم.
        /// مفتاح الكائن ASCII بحت، والاسم العربي يوضع في Content-Disposition
        /// حتى يصل للطالب اسم مقروء دون أي مخاطر ترميز في الرابط نفسه.
        /// </summary>
        public async Task<string> UploadPdfAsync(Stream content, string objectKey, string downloadFileName, CancellationToken cancellationToken)
        {
            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey,
                InputStream = content,
                ContentType = "application/pdf",

                // R2 لا ينفّذ STREAMING-AWS4-HMAC-SHA256-PAYLOAD الذي يستخدمه SDK افتراضياً
                // ويردّ عليه بخطأ "not implemented"، فنرفع بحمولة غير موقّعة.
                // الاتصال عبر HTTPS على أي حال، والتوقيع ما يزال يغطي الترويسات.
                DisablePayloadSigning = true,
            };

            request.Headers.ContentDisposition = BuildAttachmentDisposition(downloadFileName);

            await GetClient().PutObjectAsync(request, cancellationToken);

            return $"{publicBaseUrl}/{Uri.EscapeDataString(objectKey)}";
        }

        private AmazonS3Client GetClient()
        {
            if (sharedClient != null)
            {
                return sharedClient;
            }

            lock (clientLock)
            {
                sharedClient ??= new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
                {
                    ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
                    ForcePathStyle = true,

                    // SDK v4 يحسب checksums إضافية افتراضياً ويرسلها بترميز chunked لا يدعمه R2.
                    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                });
            }

            return sharedClient;
        }

        /// <summary>
        /// يبني ترويسة تفرض التنزيل باسم عربي صحيح.
        /// filename وحدها لا تحتمل غير ASCII، فنضيف filename* بترميز RFC 5987
        /// ونترك نسخة ASCII بديلة للمتصفحات التي لا تفهمها.
        /// </summary>
        private static string BuildAttachmentDisposition(string fileName)
        {
            return $"attachment; filename=\"{BuildAsciiFallback(fileName)}\"; filename*=UTF-8''{EncodeRfc5987(fileName)}";
        }

        /// <summary>
        /// اسم بديل بحروف ASCII فقط. حذف العربية يترك فراغات وشرطات متناثرة،
        /// فنضغط كل ما ليس حرفاً أو رقماً إلى شرطة سفلية واحدة.
        /// </summary>
        private static string BuildAsciiFallback(string fileName)
        {
            var withoutExtension = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;

            var builder = new System.Text.StringBuilder();
            foreach (var c in withoutExtension)
            {
                if (c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                {
                    builder.Append(c);
                }
                else if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
            }

            var name = builder.ToString().Trim('_');
            return (name.Length == 0 ? "activity" : name) + ".pdf";
        }

        // EscapeDataString يترك ' ( ) * دون ترميز، وهي ليست attr-char في RFC 5987.
        private static string EncodeRfc5987(string value) =>
            Uri.EscapeDataString(value)
                .Replace("'", "%27")
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("*", "%2A");

        private static string ReadSetting(string name, string fallback = "")
        {
            var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
