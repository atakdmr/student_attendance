using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Yoklama.Data;
using Yoklama.Models.Entities;
using Yoklama.Models.ViewModels;

namespace Yoklama.Controllers
{
    [Authorize(Roles = "Teacher,Admin")]
    public class ReportsController : Controller
    {
        private readonly AppDbContext _db;

        public ReportsController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdStr, out Guid currentUserId))
            {
                return Unauthorized();
            }

            var isAdmin = User.IsInRole("Admin");
            IQueryable<AttendanceSession> query = _db.AttendanceSessions
                .Where(s => s.Status == SessionStatus.Finalized);

            if (!isAdmin)
            {
                query = query.Where(s => s.TeacherId == currentUserId);
            }

            // Tüm session'ları yükle (filtreleme client-side yapılacak)
            var sessions = await query
                .Include(s => s.Lesson)
                .Include(s => s.Group)
                .Include(s => s.Teacher)
                .OrderByDescending(s => s.ScheduledAt)
                .ToListAsync();

            // Admin için grup listesi
            var groups = isAdmin ? await _db.Groups.OrderBy(g => g.Name).ToListAsync() : new List<Group>();

            ViewBag.Groups = groups;
            ViewBag.IsAdmin = isAdmin;

            return View(sessions);
        }

        [HttpGet]
        public async Task<IActionResult> ProcessAttendance(Guid sessionId)
        {
            var session = await _db.AttendanceSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                TempData["Error"] = "Oturum bulunamadı.";
                return RedirectToAction("Index", "Home");
            }

            // Yoklama sonrası işlemler (loglama vb.) burada yapılabilir.

            return RedirectToAction("Group", new { id = session.GroupId });
        }

        [HttpGet]
        [Route("Reports/Student")]
        public async Task<IActionResult> Student(Guid studentId, DateTime? from = null, DateTime? to = null)
        {
            // Debug: Log the received parameters
            Console.WriteLine($"Student method called with studentId: {studentId}, from: {from}, to: {to}");

            var student = await _db.Students
                .Include(s => s.Group)
                .FirstOrDefaultAsync(s => s.Id == studentId);

            if (student == null)
            {
                Console.WriteLine($"Student not found with ID: {studentId}");
                TempData["Error"] = "Öğrenci bulunamadı.";
                return RedirectToAction("Index", "Groups");
            }

            // Default date range: last 30 days
            var fromDate = from ?? DateTime.Today.AddDays(-30);
            var toDate = to ?? DateTime.Today;

            var attendanceRecords = await _db.AttendanceRecords
                .Where(ar => ar.StudentId == studentId)
                .Include(ar => ar.Session)
                .ThenInclude(s => s.Lesson)
                .ToListAsync();

            attendanceRecords = attendanceRecords
                .Where(ar => ar.Session.ScheduledAt >= fromDate && ar.Session.ScheduledAt < toDate.AddDays(1))
                .OrderByDescending(ar => ar.Session.ScheduledAt)
                .ToList();

            var vm = new StudentReportVm
            {
                Student = student,
                FromDate = fromDate,
                ToDate = toDate,
                AttendanceRecords = attendanceRecords.Select(ar => new AttendanceReportItemVm
                {
                    ScheduledAt = ar.Session.ScheduledAt,
                    LessonTitle = ar.Session.Lesson.Title,
                    Status = ar.Status,
                    LateMinutes = ar.LateMinutes,
                    Note = ar.Note,
                    SessionId = ar.SessionId
                }).ToList(),
                Summary = CalculateAttendanceSummary(attendanceRecords)
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Group(Guid id, DateTime? from = null, DateTime? to = null)
        {
            var group = await _db.Groups
                .Include(g => g.Students.Where(s => s.IsActive))
                .FirstOrDefaultAsync(g => g.Id == id);

            if (group == null)
            {
                TempData["Error"] = "Grup bulunamadı.";
                return RedirectToAction("Index", "Groups");
            }

            // Local offset belirle
            var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);

            // Default tarih aralığı: son 30 gün
            var fromDate = from.HasValue
                ? new DateTimeOffset(from.Value, localOffset)
                : DateTimeOffset.Now.AddDays(-30);

            var toDate = to.HasValue
                ? new DateTimeOffset(to.Value, localOffset)
                : DateTimeOffset.Now;

            var studentIds = group.Students.Select(s => s.Id).ToList();
            var allAttendanceRecords = await _db.AttendanceRecords
                .Include(ar => ar.Session)
                .Where(ar => studentIds.Contains(ar.StudentId) 
                          && ar.Session.ScheduledAt >= fromDate
                          && ar.Session.ScheduledAt < toDate.AddDays(1))
                .ToListAsync();

            // Kayıtları öğrenciye göre grupla
            var recordsByStudent = allAttendanceRecords
                .GroupBy(ar => ar.StudentId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var studentSummaries = new List<StudentAttendanceSummaryVm>();

            foreach (var student in group.Students)
            {
                var attendanceRecords = recordsByStudent.GetValueOrDefault(student.Id) ?? new List<AttendanceRecord>();

                studentSummaries.Add(new StudentAttendanceSummaryVm
                {
                    Student = student,
                    Summary = CalculateAttendanceSummary(attendanceRecords)
                });
            }

            var vm = new GroupReportVm
            {
                Group = group,
                FromDate = fromDate.DateTime,
                ToDate = toDate.DateTime,
                StudentSummaries = studentSummaries
                    .OrderBy(s => s.Student.LastName)
                    .ThenBy(s => s.Student.FirstName)
                    .ToList()
            };

            return View(vm);
        }



        [HttpGet]
        public async Task<IActionResult> ExportSessionCsv(Guid sessionId)
        {
            var session = await _db.AttendanceSessions
                .Include(s => s.Lesson)
                .Include(s => s.Group)
                .Include(s => s.Records)
                .ThenInclude(r => r.Student)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
            {
                TempData["Error"] = "Oturum bulunamadı.";
                return RedirectToAction("Index", "Groups");
            }

            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Öğrenci No,Ad,Soyad,Durum,Geç Kalma (dk),Not,İşaretlenme Zamanı");

            // Data rows
            foreach (var record in session.Records.OrderBy(r => r.Student.StudentNumber))
            {
                var statusText = record.Status switch
                {
                    AttendanceStatus.Present => "Mevcut",
                    AttendanceStatus.Absent => "Yok",
                    AttendanceStatus.Late => "Geç",
                    AttendanceStatus.Excused => "Mazur",
                    _ => "Bilinmiyor"
                };

                csv.AppendLine($"{EscapeCsvField(record.Student.StudentNumber)}," +
                              $"{EscapeCsvField(record.Student.FirstName)}," +
                              $"{EscapeCsvField(record.Student.LastName)}," +
                              $"{EscapeCsvField(statusText)}," +
                              $"{record.LateMinutes?.ToString() ?? ""}," +
                              $"{EscapeCsvField(record.Note ?? "")}," +
                              $"{record.MarkedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
            }

            var fileName = $"yoklama_{session.Group.Code}_{session.Lesson.Title}_{session.ScheduledAt:yyyyMMdd_HHmm}.csv";
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());

            return File(bytes, "text/csv; charset=utf-8", fileName);
        }

        [HttpGet]
        public async Task<IActionResult> ExportStudentCsv(Guid studentId, DateTime? from = null, DateTime? to = null)
        {
            var student = await _db.Students
                .Include(s => s.Group)
                .FirstOrDefaultAsync(s => s.Id == studentId);

            if (student == null)
            {
                TempData["Error"] = "Öğrenci bulunamadı.";
                return RedirectToAction("Index", "Groups");
            }

            var fromDate = from ?? DateTime.Today.AddDays(-30);
            var toDate = to ?? DateTime.Today;

            var attendanceRecords = await _db.AttendanceRecords
                .Where(ar => ar.StudentId == studentId)
                .Include(ar => ar.Session)
                .ThenInclude(s => s.Lesson)
                .ToListAsync();

            attendanceRecords = attendanceRecords
                .Where(ar => ar.Session.ScheduledAt >= fromDate && ar.Session.ScheduledAt < toDate.AddDays(1))
                .OrderByDescending(ar => ar.Session.ScheduledAt)
                .ToList();

            var csv = new StringBuilder();

            // Header
            csv.AppendLine("Tarih,Ders,Durum,Geç Kalma (dk),Not");

            // Data rows
            foreach (var record in attendanceRecords)
            {
                var statusText = record.Status switch
                {
                    AttendanceStatus.Present => "Mevcut",
                    AttendanceStatus.Absent => "Yok",
                    AttendanceStatus.Late => "Geç",
                    AttendanceStatus.Excused => "Mazur",
                    _ => "Bilinmiyor"
                };

                csv.AppendLine($"{record.Session.ScheduledAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}," +
                              $"{EscapeCsvField(record.Session.Lesson.Title)}," +
                              $"{EscapeCsvField(statusText)}," +
                              $"{record.LateMinutes?.ToString() ?? ""}," +
                              $"{EscapeCsvField(record.Note ?? "")}");
            }

            var fileName = $"ogrenci_raporu_{student.StudentNumber}_{fromDate:yyyyMMdd}_{toDate:yyyyMMdd}.csv";
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());

            return File(bytes, "text/csv; charset=utf-8", fileName);
        }

        private static AttendanceSummaryVm CalculateAttendanceSummary(List<AttendanceRecord> records)
        {
            return new AttendanceSummaryVm
            {
                TotalSessions = records.Count,
                PresentCount = records.Count(r => r.Status == AttendanceStatus.Present),
                AbsentCount = records.Count(r => r.Status == AttendanceStatus.Absent),
                LateCount = records.Count(r => r.Status == AttendanceStatus.Late),
                ExcusedCount = records.Count(r => r.Status == AttendanceStatus.Excused)
            };
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return "";

            // If field contains comma, quote, or newline, wrap in quotes and escape internal quotes
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }
    }
}
