using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Yoklama.Models;
using Yoklama.Data;
using Yoklama.Services;
using Microsoft.EntityFrameworkCore;
using Yoklama.Models.Entities;

namespace Yoklama.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly AppDbContext _db;
    private readonly IUserService _userService;

    public HomeController(ILogger<HomeController> logger, AppDbContext db, IUserService userService)
    {
        _logger = logger;
        _db = db;
        _userService = userService;
    }

    public async Task<IActionResult> Index()
    {
        if (User.Identity!.IsAuthenticated)
        {
            var currentUserId = _userService.GetCurrentUserId(User);
            if (currentUserId.HasValue)
            {
                var today = DateTimeOffset.Now.Date;
                if (User.IsInRole("Admin"))
                {
                    // Admin dashboard
                    var stats = new
                    {
                        GroupCount = await _db.Groups.CountAsync(),
                        StudentCount = await _db.Students.CountAsync(s => s.IsActive),
                        LessonCount = await _db.Lessons.CountAsync(l => l.IsActive),
                        UserCount = await _db.Users.CountAsync(u => u.IsActive)
                    };
                    ViewBag.Stats = stats;
                    ViewBag.IsAdmin = true;

                    // Bugünün derslerini al (session açılmamış olsa bile)
                    var todayDayOfWeek = (int)today.DayOfWeek;
                    if (todayDayOfWeek == 0) todayDayOfWeek = 7; // Pazar = 7

                    var todayLessons = await _db.Lessons
                        .Where(l => l.IsActive && l.DayOfWeek == todayDayOfWeek)
                        .Include(l => l.Group)
                        .Include(l => l.Teacher)
                        .OrderBy(l => l.StartTime)
                        .ToListAsync();

                    var lessonIds = todayLessons.Select(l => l.Id).ToList();
                    var todaySessions = await _db.AttendanceSessions
                        .Where(s => lessonIds.Contains(s.LessonId) && s.ScheduledAt.Date == today)
                        .Include(s => s.Lesson)
                        .Include(s => s.Group)
                        .Include(s => s.Teacher)
                        .ToListAsync();

                    // Bugünün derslerini session durumuyla birleştir
                    var lessonsWithSessions = new List<dynamic>();
                    foreach (var lesson in todayLessons)
                    {
                        var session = todaySessions.FirstOrDefault(s => s.LessonId == lesson.Id);
                        
                        lessonsWithSessions.Add(new
                        {
                            Lesson = lesson,
                            Group = lesson.Group,
                            Teacher = lesson.Teacher,
                            Session = session,
                            Status = session?.Status,
                            ScheduledAt = session?.ScheduledAt ?? today.Add(lesson.StartTime)
                        });
                    }

                    ViewBag.SessionsToday = lessonsWithSessions;

                    // Aktif duyuruları getir
                    var activeAnnouncements = await _db.Announcements
                        .Where(a => a.IsActive)
                        .Include(a => a.CreatedBy)
                        .OrderByDescending(a => a.Priority)
                        .ThenByDescending(a => a.CreatedAt)
                        .Take(5) // Son 5 duyuru
                        .ToListAsync();
                    ViewBag.ActiveAnnouncements = activeAnnouncements;
                }
                else if (User.IsInRole("Teacher"))
                {
                    // Teacher dashboard
                    var lessonCount = await _db.Lessons.CountAsync(l => l.TeacherId == currentUserId.Value && l.IsActive);
                    var sessionCount = await _db.AttendanceSessions.CountAsync(s => s.TeacherId == currentUserId.Value);
                    ViewBag.LessonCount = lessonCount;
                    ViewBag.SessionCount = sessionCount;
                    ViewBag.IsTeacher = true;

                    // Bugünün derslerini al (session açılmamış olsa bile)
                    var todayDayOfWeek = (int)today.DayOfWeek;
                    if (todayDayOfWeek == 0) todayDayOfWeek = 7; // Pazar = 7

                    var todayLessons = await _db.Lessons
                        .Where(l => l.TeacherId == currentUserId.Value && 
                                   l.IsActive && 
                                   l.DayOfWeek == todayDayOfWeek)
                        .Include(l => l.Group)
                        .OrderBy(l => l.StartTime)
                        .ToListAsync();

                    // ✅ OPTIMIZATION: Sadece bugünün session'larını çek (öğretmenin tüm session'ları yerine)
                    var lessonIds = todayLessons.Select(l => l.Id).ToList();
                    var todaySessions = await _db.AttendanceSessions
                        .Where(s => s.TeacherId == currentUserId.Value 
                                 && lessonIds.Contains(s.LessonId)
                                 && s.ScheduledAt.Date == today)
                        .Include(s => s.Lesson)
                        .Include(s => s.Group)
                        .ToListAsync();

                    // Bugünün derslerini session durumuyla birleştir
                    var lessonsWithSessions = new List<dynamic>();
                    foreach (var lesson in todayLessons)
                    {
                        var session = todaySessions.FirstOrDefault(s => s.LessonId == lesson.Id);
                        
                        lessonsWithSessions.Add(new
                        {
                            Lesson = lesson,
                            Group = lesson.Group,
                            Session = session,
                            Status = session?.Status,
                            ScheduledAt = session?.ScheduledAt ?? today.Add(lesson.StartTime)
                        });
                    }

                    ViewBag.SessionsToday = lessonsWithSessions;

                    // Aktif duyuruları getir
                    var activeAnnouncements = await _db.Announcements
                        .Where(a => a.IsActive)
                        .Include(a => a.CreatedBy)
                        .OrderByDescending(a => a.Priority)
                        .ThenByDescending(a => a.CreatedAt)
                        .Take(5) // Son 5 duyuru
                        .ToListAsync();
                    ViewBag.ActiveAnnouncements = activeAnnouncements;
                }
            }
        }
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
