using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Yoklama.Data;
using Yoklama.Models.Entities;
using Yoklama.Models.ViewModels;

namespace Yoklama.Controllers
{
    public class ScheduleController : Controller
    {
        private readonly AppDbContext _context;

        public ScheduleController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Ders Programı";
            
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
                return RedirectToAction("Login", "Account");

            var currentUser = await _context.Users.FindAsync(userId);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            // Tüm dersleri çek (filtreleme client-side yapılacak)
            var lessonsQuery = _context.Lessons
                .Where(l => l.IsActive)
                .Include(l => l.Group)
                .Include(l => l.Teacher)
                .OrderBy(l => l.DayOfWeek)
                .ThenBy(l => l.StartTime)
                .AsQueryable();

            // Sadece role bazlı filtreleme
            if (currentUser.Role != UserRole.Admin)
            {
                // Öğretmen: sadece kendi dersleri
                lessonsQuery = lessonsQuery.Where(l => l.TeacherId == currentUser.Id);
            }

            var lessons = await lessonsQuery.ToListAsync();
            

            // Önce tüm oturumları çek, sonra client-side filtrele
            var today = DateTime.Today;
            var allSessions = await _context.AttendanceSessions
                .ToListAsync();

            // Grup listesi
            var groups = currentUser.Role == UserRole.Admin 
                ? await _context.Groups.OrderBy(g => g.Name).ToListAsync() 
                : await _context.Lessons
                    .Where(l => l.TeacherId == currentUser.Id)
                    .Include(l => l.Group)
                    .Select(l => l.Group)
                    .Distinct()
                    .OrderBy(g => g.Name)
                    .ToListAsync();

            // Haftalık ders programı için günleri oluştur
            var days = new List<ScheduleDayVm>();
            var dayNames = new[] { "", "Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi", "Pazar" };
            
            for (int i = 1; i <= 7; i++)
            {
                var dayLessons = lessons.Where(l => l.DayOfWeek == i).ToList();
                var scheduleLessons = dayLessons.Select(l => {
                    // Bugünün session'ını bul
                    var todaySession = allSessions
                        .Where(s => s.LessonId == l.Id && 
                                   s.ScheduledAt.Date == today)
                        .FirstOrDefault();

                    // Bugün oturum yoksa, haftalık oturumu ara
                    if (todaySession == null)
                    {
                        var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
                        var weekEnd = weekStart.AddDays(7);
                        
                        todaySession = allSessions
                            .Where(s => s.LessonId == l.Id && 
                                       s.ScheduledAt.Date >= weekStart && 
                                       s.ScheduledAt.Date < weekEnd &&
                                       s.Status != SessionStatus.Finalized)
                            .OrderByDescending(s => s.ScheduledAt)
                            .FirstOrDefault();
                    }

                    return new ScheduleLessonVm
                    {
                        LessonId = l.Id,
                        Title = l.Title,
                        StartTime = l.StartTime,
                        EndTime = l.EndTime,
                        GroupName = l.Group?.Name ?? "Grup Yok",
                        TeacherName = l.Teacher?.FullName ?? "Öğretmen Yok",
                        TeacherId = l.TeacherId,
                        SessionStatus = todaySession?.Status,
                        SessionId = todaySession?.Id,
                        IsActive = l.IsActive
                    };
                }).ToList();

                days.Add(new ScheduleDayVm
                {
                    DayOfWeek = i,
                    DayName = dayNames[i],
                    Lessons = scheduleLessons
                });
            }

            var vm = new ScheduleVm
            {
                Days = days,
                Groups = groups,
                IsAdmin = currentUser.Role == UserRole.Admin
            };

            return View(vm);
        }
    }
}