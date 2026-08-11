using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TaskImagesToPdfFunctionApp
{
    /// <summary>
    /// معلومات الفصل الدراسي التي تُطبع في صفحة الغلاف.
    /// </summary>
    internal sealed class SemesterInfo
    {
        public string SemesterCode { get; set; } = string.Empty;

        /// <summary>"الفصل الدراسي الثاني" أو "الفصل الصيفي".</summary>
        public string SemesterFullName { get; set; } = string.Empty;

        /// <summary>"2025/2026".</summary>
        public string AcademicYear { get; set; } = string.Empty;

        /// <summary>سطر الغلاف: "حل النشاط للفصل الصيفي 1253".</summary>
        public string HeaderLine =>
            string.IsNullOrWhiteSpace(SemesterFullName)
                ? "حل النشاط"
                : $"حل النشاط {_WithLamPrefix(SemesterFullName)} {SemesterCode}".TrimEnd();

        // "الفصل الصيفي" => "للفصل الصيفي" (ألف "ال" تسقط بعد لام الجر).
        private static string _WithLamPrefix(string name) =>
            name.StartsWith("ال", StringComparison.Ordinal) ? "ل" + name.Substring(1) : "لـ" + name;

        /// <summary>يبني المعلومات من رمز الفصل وحده (1253) دون الحاجة للـ API.</summary>
        public static SemesterInfo FromCode(string semesterCode)
        {
            var name = semesterCode[^1] switch
            {
                '1' => "الأول",
                '2' => "الثاني",
                '3' => "الصيفي",
                _ => string.Empty
            };

            int year = 2000 + int.Parse(semesterCode.Substring(1, 2));

            return new SemesterInfo
            {
                SemesterCode = semesterCode,
                SemesterFullName = semesterCode.EndsWith("3") ? "الفصل الصيفي" : $"الفصل الدراسي {name}",
                AcademicYear = $"{year}/{year + 1}"
            };
        }

        public static bool IsValidCode(string? semesterCode) =>
            !string.IsNullOrWhiteSpace(semesterCode)
            && semesterCode.Length == 4
            && semesterCode.All(char.IsDigit)
            && semesterCode[^1] is '1' or '2' or '3';
    }

    /// <summary>
    /// يجلب الفصل الدراسي الحالي من واجهة زمايل بدل تثبيته في الكود،
    /// مع تخزين مؤقت لأن القيمة تتغير مرة كل بضعة أشهر فقط.
    /// </summary>
    internal static class CurrentSemesterProvider
    {
        private const string DefaultApiUrl =
            "https://zamayl7.azurewebsites.net/api/AcademicCalendar/Qou/current-semester";

        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan successTtl = TimeSpan.FromHours(6);

        // بعد فشل الجلب نعيد المحاولة بعد فترة قصيرة، حتى لا يدفع كل طلب تكلفة انتظار خدمة معطّلة.
        private static readonly TimeSpan failureRetryDelay = TimeSpan.FromMinutes(15);
        private static readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private static SemesterInfo? cached;
        private static DateTimeOffset nextAttemptAt;

        public static async Task<SemesterInfo> GetAsync(ILogger logger, CancellationToken cancellationToken = default)
        {
            if (_IsCacheUsable())
                return cached!;

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (_IsCacheUsable())
                    return cached!;

                var fetched = await _FetchAsync(logger, cancellationToken);
                if (fetched != null)
                {
                    cached = fetched;
                    nextAttemptAt = DateTimeOffset.UtcNow + successTtl;
                    return fetched;
                }

                nextAttemptAt = DateTimeOffset.UtcNow + failureRetryDelay;

                // تعذّر الجلب: نُبقي آخر قيمة ناجحة حتى لو انتهت صلاحيتها،
                // وإن لم تكن هناك أي قيمة نستنتج الفصل من التاريخ كحل أخير.
                if (cached != null)
                {
                    logger.LogWarning("Falling back to the stale cached semester {SemesterCode}.", cached.SemesterCode);
                    return cached;
                }

                cached = _EstimateFromToday();
                logger.LogWarning("Falling back to the date-based semester estimate {SemesterCode}.", cached.SemesterCode);
                return cached;
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool _IsCacheUsable() =>
            cached != null && DateTimeOffset.UtcNow < nextAttemptAt;

        private static async Task<SemesterInfo?> _FetchAsync(ILogger logger, CancellationToken cancellationToken)
        {
            var url = Environment.GetEnvironmentVariable("CurrentSemesterApiUrl", EnvironmentVariableTarget.Process);
            if (string.IsNullOrWhiteSpace(url))
                url = DefaultApiUrl;

            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Current-semester endpoint returned {StatusCode}.", (int)response.StatusCode);
                    return null;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                var dto = JsonSerializer.Deserialize<CurrentSemesterResponse>(payload, jsonOptions);

                if (dto == null || !SemesterInfo.IsValidCode(dto.SemesterCode))
                {
                    logger.LogWarning("Current-semester endpoint returned an unusable payload.");
                    return null;
                }

                return new SemesterInfo
                {
                    SemesterCode = dto.SemesterCode!,
                    // نعتمد على اسم الفصل القادم من الـ API، ونشتقه من الرمز إن جاء فارغاً.
                    SemesterFullName = string.IsNullOrWhiteSpace(dto.SemesterFullName)
                        ? SemesterInfo.FromCode(dto.SemesterCode!).SemesterFullName
                        : dto.SemesterFullName!,
                    AcademicYear = string.IsNullOrWhiteSpace(dto.AcademicYear)
                        ? SemesterInfo.FromCode(dto.SemesterCode!).AcademicYear
                        : dto.AcademicYear!
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch the current semester.");
                return null;
            }
        }

        /// <summary>
        /// حل أخير حين يتعذّر الوصول للـ API ولا توجد قيمة مخزّنة:
        /// تقدير الفصل من الشهر حسب تقويم الجامعة المعتاد.
        /// </summary>
        private static SemesterInfo _EstimateFromToday()
        {
            // توقيت فلسطين (UTC+2/+3) وليس توقيت الخادم.
            var today = DateTime.UtcNow.AddHours(3);

            int academicYearStart;
            char term;

            if (today.Month >= 9)                    // أيلول - كانون الأول: الفصل الأول
            {
                academicYearStart = today.Year;
                term = '1';
            }
            else if (today.Month == 1)               // كانون الثاني: تتمة الفصل الأول
            {
                academicYearStart = today.Year - 1;
                term = '1';
            }
            else if (today.Month < 6 || (today.Month == 6 && today.Day <= 10)) // شباط - أوائل حزيران: الفصل الثاني
            {
                academicYearStart = today.Year - 1;
                term = '2';
            }
            else                                     // منتصف حزيران - آب: الفصل الصيفي
            {
                academicYearStart = today.Year - 1;
                term = '3';
            }

            return SemesterInfo.FromCode($"1{academicYearStart % 100:D2}{term}");
        }

        private sealed class CurrentSemesterResponse
        {
            public string? SemesterCode { get; set; }
            public string? SemesterFullName { get; set; }
            public string? AcademicYear { get; set; }
        }
    }
}
