using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Yoklama.Data;
using Yoklama.Models.Entities;
using Yoklama.Models.ViewModels;
using Yoklama.Services;

namespace Yoklama.Controllers
{
    [Authorize(Roles = "Teacher,Admin")]
    public class LessonsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IAttendanceService _attendanceService;
        private readonly IUserService _userService;

        public LessonsController(AppDbContext db, IAttendanceService attendanceService, IUserService userService)
        {
            _db = db;
            _attendanceService = attendanceService;
            _userService = userService;
        }

        public async Task<IActionResult> Index()
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var isAdmin = User.IsInRole(UserRole.Admin.ToString());
            var query = _db.Lessons.Where(l => l.IsActive);

            // Admin olmayan kullanıcılar sadece kendi derslerini görür
            if (!isAdmin)
            {
                query = query.Where(l => l.TeacherId == currentUserId.Value);
            }

            // Tüm dersleri yükle (filtreleme client-side yapılacak)
            var lessons = await query
                .Include(l => l.Group)
                .Include(l => l.Teacher)
                .OrderBy(l => l.DayOfWeek)
                .ThenBy(l => l.StartTime)
                .ToListAsync();

            // Teacher bilgilerini manuel olarak yükle (Include çalışmıyor)
            var teacherIds = lessons.Select(l => l.TeacherId).Distinct().ToList();
            var teachers = await _db.Users.Where(u => teacherIds.Contains(u.Id)).ToListAsync();

            // Debug: Teacher verilerini kontrol et
            Console.WriteLine($"Found {teachers.Count} teachers for {teacherIds.Count} teacher IDs");
            foreach (var teacher in teachers)
            {
                Console.WriteLine($"Teacher: {teacher.Id} - FullName: '{teacher.FullName}' (Length: {teacher.FullName?.Length ?? 0})");
            }

            // Bugünün tarihini al
            var today = DateTimeOffset.Now.Date;
            var todayDayOfWeek = (int)today.DayOfWeek;
            if (todayDayOfWeek == 0) todayDayOfWeek = 7; // Pazar = 7

            // Tüm oturumları çek (SQLite DateTimeOffset sorunu için tarih filtrelemesi yapmıyoruz)
            var allSessions = await _db.AttendanceSessions
                .ToListAsync();

            // Her ders için oturum durumunu kontrol et
            var lessonsWithSessions = new List<LessonWithSessionVm>();

            foreach (var lesson in lessons)
            {
                // Teacher bilgisini manuel olarak bul ve ata
                var teacher = teachers.FirstOrDefault(t => t.Id == lesson.TeacherId);

                var lessonVm = new LessonWithSessionVm
                {
                    Id = lesson.Id,
                    Title = lesson.Title,
                    DayOfWeek = lesson.DayOfWeek,
                    StartTime = lesson.StartTime,
                    EndTime = lesson.EndTime,
                    IsActive = lesson.IsActive,
                    Group = lesson.Group,
                    Teacher = teacher // Doğrudan teachers listesinden al
                };

                Console.WriteLine($"Lesson {lesson.Title}: TeacherId={lesson.TeacherId}, Teacher={teacher?.FullName ?? "NULL"} (Teacher object: {teacher != null})");

                // Bu ders için bugünün oturumunu kontrol et
                var todaySession = allSessions
                    .Where(s => s.LessonId == lesson.Id &&
                               s.ScheduledAt.Date == today)
                    .FirstOrDefault();

                // Bugün oturum yoksa, haftalık oturumu ara
                if (todaySession == null)
                {
                    var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
                    var weekEnd = weekStart.AddDays(7);

                    todaySession = allSessions
                        .Where(s => s.LessonId == lesson.Id &&
                                   s.ScheduledAt.Date >= weekStart &&
                                   s.ScheduledAt.Date < weekEnd &&
                                   s.Status != SessionStatus.Finalized)
                        .OrderByDescending(s => s.ScheduledAt)
                        .FirstOrDefault();
                }

                if (todaySession != null)
                {
                    lessonVm.SessionId = todaySession.Id;
                    lessonVm.SessionStatus = todaySession.Status;
                }

                lessonsWithSessions.Add(lessonVm);
            }

            // Filtreleme verilerini yükle
            var groups = new List<Group>();
            var teachersList = new List<User>();

            if (isAdmin)
            {
                // Admin için tüm gruplar ve öğretmenler
                groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
                teachersList = await _db.Users
                    .Where(u => (u.Role == UserRole.Teacher || u.Role == UserRole.Admin) && u.IsActive)
                    .OrderBy(u => u.FullName)
                    .ToListAsync();
            }
            else
            {
                // Öğretmen için sadece kendi derslerinin grupları
                var lessonGroupIds = lessons.Select(l => l.GroupId).Distinct().ToList();
                groups = await _db.Groups
                    .Where(g => lessonGroupIds.Contains(g.Id))
                    .OrderBy(g => g.Name)
                    .ToListAsync();
            }

            ViewBag.Groups = groups;
            ViewBag.Teachers = teachersList;

            // Admin için tüm dersleri göster (edit yok, sadece görüntüleme)
            if (isAdmin)
            {
                var vm = new LessonsVm
                {
                    Lessons = lessonsWithSessions,
                    IsAdmin = isAdmin
                };

                return View(vm);
            }

            var vm2 = new LessonsVm
            {
                Lessons = lessonsWithSessions,
                IsAdmin = isAdmin
            };

            return View(vm2);
        }

        // GET: /Lessons/OpenSession?lessonId={id}&scheduledAt=yyyy-MM-ddTHH:mm (optional)
        [HttpGet]
        public async Task<IActionResult> OpenSession(Guid lessonId, DateTimeOffset? scheduledAt)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var lesson = await _db.Lessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId && l.IsActive);
            if (lesson == null) return NotFound();

            // Authorization: Teacher can only open their own lessons; Admin can open any
            var isAdmin = User.IsInRole(UserRole.Admin.ToString());
            if (!isAdmin && lesson.TeacherId != currentUserId.Value)
            {
                return Forbid();
            }

            // Compute default scheduledAt when not provided:
            // Use today's date with the lesson's StartTime; if day of week differs, pick the next occurrence of lesson.DayOfWeek.
            var when = scheduledAt ?? ComputeNextOccurrenceWithTime(lesson.DayOfWeek, lesson.StartTime);

            var session = await _attendanceService.OpenOrGetSessionAsync(lessonId, when, currentUserId.Value);
            return RedirectToAction("Session", "Attendance", new { sessionId = session.Id });
        }

        // GET: /Lessons/CreateLesson
        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateLesson(Guid? groupId)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var vm = new CreateEditVm
            {
                TeacherId = currentUserId.Value,
                IsActive = true
            };

            if (groupId.HasValue)
            {
                vm.GroupId = groupId.Value;
            }

            var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
            ViewBag.Groups = groups;
            if (User.IsInRole(UserRole.Admin.ToString()))
            {
                // Allow assigning lessons to both Teachers and Admins in create view
                var teachers = await _db.Users.Where(u => (u.Role == UserRole.Teacher || u.Role == UserRole.Admin) && u.IsActive)
                    .OrderBy(u => u.FullName)
                    .ToListAsync();
                ViewBag.Teachers = teachers;
            }
            else
            {
                // For non-admin users, set current user as the only teacher option
                var currentUser = await _db.Users.FindAsync(currentUserId.Value);
                if (currentUser != null)
                {
                    ViewBag.Teachers = new List<User> { currentUser };
                }
            }
            return View("Create", vm);
        }

        // POST: /Lessons/CreateLesson
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateLesson(CreateEditVm vm)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            // For non-admin users, ensure TeacherId is set to current user
            if (!User.IsInRole(UserRole.Admin.ToString()))
            {
                vm.TeacherId = currentUserId.Value;
            }

            if (vm.GroupId == Guid.Empty)
            {
                ModelState.AddModelError("GroupId", "Grup seçmelisiniz.");
            }

            if (vm.TeacherId == Guid.Empty)
            {
                ModelState.AddModelError("TeacherId", "Öğretmen seçmelisiniz.");
            }

            if (string.IsNullOrWhiteSpace(vm.Title))
            {
                ModelState.AddModelError("Title", "Ders başlığı gereklidir.");
            }

            if (vm.DayOfWeek < 1 || vm.DayOfWeek > 7)
            {
                ModelState.AddModelError("DayOfWeek", "Geçerli bir gün seçin.");
            }

            if (vm.StartTime == TimeSpan.Zero)
            {
                ModelState.AddModelError("StartTime", "Başlangıç saati gereklidir.");
            }

            if (vm.EndTime == TimeSpan.Zero)
            {
                ModelState.AddModelError("EndTime", "Bitiş saati gereklidir.");
            }

            if (vm.EndTime <= vm.StartTime)
            {
                ModelState.AddModelError("EndTime", "Bitiş saati başlangıç saatinden sonra olmalıdır.");
            }

            if (vm.GroupId != Guid.Empty && !await _db.Groups.AnyAsync(g => g.Id == vm.GroupId))
            {
                ModelState.AddModelError("GroupId", "Seçilen grup bulunamadı.");
            }

            if (vm.TeacherId != Guid.Empty && !await _db.Users.AnyAsync(u => u.Id == vm.TeacherId))
            {
                ModelState.AddModelError("TeacherId", "Seçilen öğretmen bulunamadı.");
            }

            if (!ModelState.IsValid)
            {
                var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
                var teachers = await _db.Users.Where(u => u.Role == UserRole.Teacher).OrderBy(u => u.FullName).ToListAsync();
                ViewBag.Groups = groups;
                ViewBag.Teachers = teachers;
                return View("Create", vm);
            }

            var lesson = new Lesson
            {
                Title = vm.Title,
                DayOfWeek = vm.DayOfWeek,
                StartTime = vm.StartTime,
                EndTime = vm.EndTime,
                GroupId = vm.GroupId,
                TeacherId = vm.TeacherId,
                IsActive = vm.IsActive
            };

            _db.Lessons.Add(lesson);
            await _db.SaveChangesAsync();

            TempData["Success"] = "Ders başarıyla eklendi.";
            return RedirectToAction("Index");
        }

        // GET: /Lessons/EditLesson/{id}
        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> EditLesson(Guid id)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var lesson = await _db.Lessons.Include(l => l.Group).FirstOrDefaultAsync(l => l.Id == id);
            if (lesson == null) return NotFound();

            var isAdmin = User.IsInRole(UserRole.Admin.ToString());
            if (!isAdmin && lesson.TeacherId != currentUserId.Value)
            {
                return Forbid();
            }

            var vm = new CreateEditVm
            {
                Id = lesson.Id,
                Title = lesson.Title,
                DayOfWeek = lesson.DayOfWeek,
                StartTime = lesson.StartTime,
                EndTime = lesson.EndTime,
                GroupId = lesson.GroupId,
                TeacherId = lesson.TeacherId,
                IsActive = lesson.IsActive
            };

            var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
            ViewBag.Groups = groups;

            if (User.IsInRole(UserRole.Admin.ToString()))
            {
                // Allow assigning lessons to both Teachers and Admins
                var teachers = await _db.Users.Where(u => (u.Role == UserRole.Teacher || u.Role == UserRole.Admin) && u.IsActive)
                    .OrderBy(u => u.FullName)
                    .ToListAsync();
                ViewBag.Teachers = teachers;
            }

            return View("Edit", vm);
        }

        // POST: /Lessons/EditLesson
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> EditLesson(CreateEditVm vm)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var existingLesson = await _db.Lessons.FirstOrDefaultAsync(l => l.Id == vm.Id);
            if (existingLesson == null) return NotFound();

            var isAdmin = User.IsInRole(UserRole.Admin.ToString());
            if (!isAdmin && existingLesson.TeacherId != currentUserId.Value)
            {
                return Forbid();
            }

            // For non-admin users, ensure TeacherId remains the same
            if (!isAdmin)
            {
                vm.TeacherId = existingLesson.TeacherId;
            }

            if (vm.TeacherId == Guid.Empty)
            {
                ModelState.AddModelError("TeacherId", "Öğretmen seçmelisiniz.");
            }

            if (string.IsNullOrWhiteSpace(vm.Title))
            {
                ModelState.AddModelError("Title", "Ders başlığı gereklidir.");
            }

            if (vm.DayOfWeek < 1 || vm.DayOfWeek > 7)
            {
                ModelState.AddModelError("DayOfWeek", "Geçerli bir gün seçin.");
            }

            if (vm.StartTime == TimeSpan.Zero)
            {
                ModelState.AddModelError("StartTime", "Başlangıç saati gereklidir.");
            }

            if (vm.EndTime == TimeSpan.Zero)
            {
                ModelState.AddModelError("EndTime", "Bitiş saati gereklidir.");
            }

            if (vm.EndTime <= vm.StartTime)
            {
                ModelState.AddModelError("EndTime", "Bitiş saati başlangıç saatinden sonra olmalıdır.");
            }

            if (!await _db.Groups.AnyAsync(g => g.Id == vm.GroupId))
            {
                ModelState.AddModelError("GroupId", "Seçilen grup bulunamadı.");
            }

            if (vm.TeacherId != Guid.Empty && !await _db.Users.AnyAsync(u => u.Id == vm.TeacherId))
            {
                ModelState.AddModelError("TeacherId", "Seçilen öğretmen bulunamadı.");
            }

            if (!ModelState.IsValid)
            {
                var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
                ViewBag.Groups = groups;

                if (User.IsInRole(UserRole.Admin.ToString()))
                {
                    var teachers = await _db.Users.Where(u => u.Role == UserRole.Teacher).OrderBy(u => u.FullName).ToListAsync();
                    ViewBag.Teachers = teachers;
                }

                return View("Edit", vm);
            }

            existingLesson.Title = vm.Title;
            existingLesson.DayOfWeek = vm.DayOfWeek;
            existingLesson.StartTime = vm.StartTime;
            existingLesson.EndTime = vm.EndTime;
            existingLesson.GroupId = vm.GroupId;
            existingLesson.TeacherId = vm.TeacherId;
            existingLesson.IsActive = true; // Always keep lessons active

            await _db.SaveChangesAsync();

            TempData["Success"] = "Ders başarıyla güncellendi.";
            return RedirectToAction("Index");
        }

        // POST: /Lessons/DeleteLesson
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteLesson(Guid id)

        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var lesson = await _db.Lessons.FirstOrDefaultAsync(l => l.Id == id);
            if (lesson == null) return NotFound();

            var isAdmin = User.IsInRole(UserRole.Admin.ToString());
            if (!isAdmin && lesson.TeacherId != currentUserId.Value)
            {
                return Forbid();
            }

            _db.Lessons.Remove(lesson);
            await _db.SaveChangesAsync();

            TempData["Success"] = "Ders başarıyla silindi.";
            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize(Roles = "Teacher")]
        public async Task<IActionResult> OpenSession(Guid lessonId)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var lesson = await _db.Lessons
                .Include(l => l.Group)
                .FirstOrDefaultAsync(l => l.Id == lessonId);

            if (lesson == null) return NotFound();

            // Öğretmen kontrolü - öğretmenler sadece kendi derslerini açabilir
            var isAdmin = User.IsInRole(UserRole.Admin.ToString());
            if (!isAdmin && lesson.TeacherId != currentUserId.Value)
                return Forbid();

            // Bugünün tarihini kontrol et
            var today = DateTimeOffset.Now.Date;
            var todayDayOfWeek = (int)today.DayOfWeek;
            if (todayDayOfWeek == 0) todayDayOfWeek = 7; // Pazar = 7

            if (lesson.DayOfWeek != todayDayOfWeek)
                return BadRequest("Bu ders bugün değil!");

            // Zaten açık oturum var mı kontrol et - DateTimeOffset için SQLite uyumlu sorgu
            var allSessions = await _db.AttendanceSessions.ToListAsync();

            // Önce bugünün tam oturumunu ara
            var existingOpenSession = allSessions
                .FirstOrDefault(s => s.LessonId == lesson.Id &&
                    s.Status == SessionStatus.Open &&
                    s.ScheduledAt.Date == today);

            // Bugün oturum yoksa, haftalık oturumu ara
            if (existingOpenSession == null)
            {
                var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
                var weekEnd = weekStart.AddDays(7);

                existingOpenSession = allSessions
                    .FirstOrDefault(s => s.LessonId == lesson.Id &&
                        s.Status == SessionStatus.Open &&
                        s.ScheduledAt.Date >= weekStart && s.ScheduledAt.Date < weekEnd);
            }

            if (existingOpenSession != null)
            {
                // Eğer açık oturum varsa, o oturuma yönlendir
                return RedirectToAction("Session", "Attendance", new { sessionId = existingOpenSession.Id });
            }

            // Yeni oturum oluştur
            var session = new AttendanceSession
            {
                LessonId = lesson.Id,
                ScheduledAt = ComputeNextOccurrenceWithTime(lesson.DayOfWeek, lesson.StartTime),
                Status = SessionStatus.Open,
                CreatedAt = DateTime.Now,
                TeacherId = currentUserId.Value,
                GroupId = lesson.GroupId
            };

            _db.AttendanceSessions.Add(session);
            await _db.SaveChangesAsync();

            return RedirectToAction("Session", "Attendance", new { sessionId = session.Id });
        }

        private static DateTimeOffset ComputeNextOccurrenceWithTime(int lessonDayOfWeek, TimeSpan startTime)
        {
            // ISO day: Monday=1 .. Sunday=7
            var today = DateTimeOffset.Now;
            int todayIso = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;

            // If today is the lesson day, check if we already have a session for today
            if (todayIso == lessonDayOfWeek)
            {
                var targetDate = today.Date.Add(startTime);
                return new DateTimeOffset(targetDate, today.Offset);
            }

            // Otherwise, find the next occurrence of this day
            int deltaDays = (lessonDayOfWeek - todayIso + 7) % 7;
            if (deltaDays == 0) deltaDays = 7; // Next week

            var targetDateNext = today.Date.AddDays(deltaDays).Add(startTime);
            return new DateTimeOffset(targetDateNext, today.Offset);
        }
    }
}
