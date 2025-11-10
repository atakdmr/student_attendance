using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Yoklama.Data;
using Yoklama.Services;
using Yoklama.Models.ViewModels;
using Yoklama.Models.Entities;

namespace Yoklama.Controllers
{
    [Authorize(Roles = "Teacher,Admin")]
    public class AttendanceController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IAttendanceService _attendanceService;
        private readonly IUserService _userService;

        public AttendanceController(AppDbContext db, IAttendanceService attendanceService, IUserService userService)
        {
            _db = db;
            _attendanceService = attendanceService;
            _userService = userService;
        }

        public async Task<IActionResult> Index(Guid? groupId = null, Guid? teacherId = null, int? dayOfWeek = null, string? lessonTitle = null)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            var isAdmin = User.IsInRole("Admin");
            IEnumerable<AttendanceSession> sessions;
            if (isAdmin)
            {
                var sessionsQuery = _db.AttendanceSessions
                    .AsNoTracking()
                    .Include(s => s.Lesson)
                    .Include(s => s.Group)
                    .Include(s => s.Teacher)
                    .AsQueryable()
                    .Where(s => s.Status != SessionStatus.Finalized);

                if (groupId.HasValue)
                {
                    sessionsQuery = sessionsQuery.Where(s => s.GroupId == groupId.Value);
                }
                if (teacherId.HasValue)
                {
                    sessionsQuery = sessionsQuery.Where(s => s.TeacherId == teacherId.Value);
                }
                if (dayOfWeek.HasValue)
                {
                    sessionsQuery = sessionsQuery.Where(s => s.Lesson != null && s.Lesson.DayOfWeek == dayOfWeek.Value);
                }
                if (!string.IsNullOrWhiteSpace(lessonTitle))
                {
                    var title = lessonTitle.Trim();
                    sessionsQuery = sessionsQuery.Where(s => s.Lesson != null && EF.Functions.Like(s.Lesson.Title, "%" + title + "%"));
                }

                sessions = await sessionsQuery
                    .OrderByDescending(s => s.Id)
                    .ToListAsync();
            }
            else
            {
                sessions = await _attendanceService.GetSessionsForTeacherAsync(currentUserId.Value);

                var today = DateTimeOffset.Now.Date;
                var todayIso = (int)today.DayOfWeek == 0 ? 7 : (int)today.DayOfWeek;
                var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
                var weekEnd = weekStart.AddDays(7);

                var teacherLessons = await _db.Lessons
                    .AsNoTracking()
                    .Include(l => l.Group)
                    .Where(l => l.IsActive && l.TeacherId == currentUserId.Value)
                    .OrderBy(l => l.DayOfWeek).ThenBy(l => l.StartTime)
                    .ToListAsync();

                // Apply filters for teacher view (group/day/title) on lessons
                if (groupId.HasValue)
                {
                    teacherLessons = teacherLessons.Where(l => l.GroupId == groupId.Value).ToList();
                }
                if (dayOfWeek.HasValue)
                {
                    teacherLessons = teacherLessons.Where(l => l.DayOfWeek == dayOfWeek.Value).ToList();
                }
                if (!string.IsNullOrWhiteSpace(lessonTitle))
                {
                    var title = lessonTitle.Trim();
                    teacherLessons = teacherLessons.Where(l => l.Title != null && EF.Functions.Like(l.Title, "%" + title + "%")).ToList();
                }

                var lessonIds = teacherLessons.Select(l => l.Id).ToList();
                
                // Bugün ve bu haftanın tüm session'larını tek sorguda çek
                var allSessions = await _db.AttendanceSessions
                    .AsNoTracking()
                    .Where(s => lessonIds.Contains(s.LessonId) 
                             && s.ScheduledAt.Date >= weekStart 
                             && s.ScheduledAt.Date < weekEnd)
                    .ToListAsync();

                // Bugünün dersleri
                var todayLessons = teacherLessons
                    .Where(l => l.DayOfWeek == todayIso)
                    .Select(l =>
                    {
                        var todaySession = allSessions
                            .FirstOrDefault(s => s.LessonId == l.Id && s.ScheduledAt.Date == today);

                        if (todaySession == null)
                        {
                            todaySession = allSessions
                                .Where(s => s.LessonId == l.Id && s.Status != SessionStatus.Finalized)
                                .OrderByDescending(s => s.ScheduledAt)
                                .FirstOrDefault();
                        }

                        return new LessonWithSessionVm
                        {
                            Id = l.Id,
                            Title = l.Title,
                            DayOfWeek = l.DayOfWeek,
                            StartTime = l.StartTime,
                            EndTime = l.EndTime,
                            IsActive = l.IsActive,
                            Group = l.Group,
                            SessionId = todaySession?.Id,
                            SessionStatus = todaySession?.Status
                        };
                    })
                    .ToList();

                // Tüm dersler (haftalık durum)
                var allLessons = teacherLessons
                    .Select(l =>
                    {
                        var weekSession = allSessions
                            .Where(s => s.LessonId == l.Id)
                            .OrderByDescending(s => s.ScheduledAt)
                            .FirstOrDefault();

                        return new LessonWithSessionVm
                        {
                            Id = l.Id,
                            Title = l.Title,
                            DayOfWeek = l.DayOfWeek,
                            StartTime = l.StartTime,
                            EndTime = l.EndTime,
                            IsActive = l.IsActive,
                            Group = l.Group,
                            SessionId = weekSession?.Id,
                            SessionStatus = weekSession?.Status
                        };
                    })
                    .ToList();

                ViewBag.TodayLessons = todayLessons;
                ViewBag.AllLessons = allLessons;
            }

            // Admin için grup listesi
            var groups = await _db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
            var teachers = isAdmin ? await _db.Users.AsNoTracking().Where(u => u.Role == UserRole.Teacher).OrderBy(u => u.FullName).ToListAsync() : new List<User>();

            ViewBag.Groups = groups;
            ViewBag.SelectedGroupId = groupId;
            ViewBag.Teachers = teachers;
            ViewBag.SelectedTeacherId = teacherId;
            ViewBag.SelectedDayOfWeek = dayOfWeek;
            ViewBag.SelectedLessonTitle = lessonTitle;
            ViewBag.IsAdmin = isAdmin;

            return View(sessions);
        }

        [HttpGet]
        public async Task<IActionResult> Session(Guid sessionId)
        {
            var vm = await _attendanceService.GetSessionVmAsync(sessionId);
            if (vm == null) return NotFound();
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Teacher")]
        public async Task<IActionResult> BulkMark(BulkMarkVm vm)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Form doğrulama hatası.";
                return RedirectToAction(nameof(Session), new { sessionId = vm.SessionId });
            }

            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            try
            {
                await _attendanceService.BulkMarkAsync(vm, currentUserId.Value);
                TempData["Success"] = "Yoklama kaydedildi.";
            }
            catch (DbUpdateConcurrencyException)
            {
                TempData["Error"] = "Bu kayıt başka biri tarafından değiştirildi. Lütfen sayfayı yenileyin.";
            }
            catch (UnauthorizedAccessException ex)
            {
                TempData["Error"] = ex.Message;
                return Forbid();
            }
            catch (Exception ex)
            {
                TempData["Error"] = "İşlem sırasında hata oluştu: " + ex.Message;
            }

            return RedirectToAction(nameof(Session), new { sessionId = vm.SessionId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Teacher")]
        public async Task<IActionResult> Mark(Guid sessionId, StudentAttendanceVm student)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            try
            {
                await _attendanceService.MarkAsync(sessionId, student, currentUserId.Value);
                TempData["Success"] = "Öğrenci yoklama kaydı güncellendi.";
            }
            catch (DbUpdateConcurrencyException)
            {
                TempData["Error"] = "Bu kayıt başka biri tarafından değiştirildi. Lütfen sayfayı yenileyin.";
            }
            catch (UnauthorizedAccessException ex)
            {
                TempData["Error"] = ex.Message;
                return Forbid();
            }
            catch (Exception ex)
            {
                TempData["Error"] = "İşlem sırasında hata oluştu: " + ex.Message;
            }

            return RedirectToAction(nameof(Session), new { sessionId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Teacher")]
        public async Task<IActionResult> CloseSession(Guid sessionId)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId == null) return Unauthorized();

            try
            {
                await _attendanceService.FinalizeSessionAsync(sessionId, currentUserId.Value);
                TempData["Success"] = "Oturum kapatıldı.";
            }
            catch (UnauthorizedAccessException ex)
            {
                TempData["Error"] = ex.Message;
                return Forbid();
            }
            catch (Exception ex)
            {
                TempData["Error"] = "İşlem sırasında hata oluştu: " + ex.Message;
            }

            return RedirectToAction(nameof(Session), new { sessionId });
        }

    }
}
