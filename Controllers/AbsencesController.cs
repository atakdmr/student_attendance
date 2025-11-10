using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Yoklama.Data;
using Yoklama.Models.Entities;
using Yoklama.Services.Sms;

namespace Yoklama.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AbsencesController : Controller
    {
        private readonly AppDbContext _context;
        private readonly ISmsService _smsService;

        public AbsencesController(AppDbContext context, ISmsService smsService)
        {
            _context = context;
            _smsService = smsService;
        }

        // Lists students who are Absent in finalized sessions
        public async Task<IActionResult> Index()
        {
            // Tüm absent kayıtlarını yükle (filtreleme client-side yapılacak)
            var absents = await _context.AttendanceRecords
                .Where(r => r.Status == AttendanceStatus.Absent && r.Session.Status == SessionStatus.Finalized)
                .Include(r => r.Student)
                .Include(r => r.Session)
                    .ThenInclude(s => s.Lesson)
                        .ThenInclude(l => l.Group)
                .OrderByDescending(r => r.Session.ScheduledAt)
                .AsNoTracking()
                .ToListAsync();

            // Get groups for filter dropdown
            var groups = await _context.Groups
                .Where(g => g.Students.Any())
                .OrderBy(g => g.Name)
                .Select(g => new { g.Id, g.Name })
                .ToListAsync();

            ViewBag.Groups = groups;

            return View(absents);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendSms()
        {
            var absents = await _context.AttendanceRecords
                .Where(r => r.Status == AttendanceStatus.Absent && r.Session.Status == SessionStatus.Finalized)
                .Include(r => r.Student)
                .Include(r => r.Session)
                    .ThenInclude(s => s.Lesson)
                        .ThenInclude(l => l.Group)
                .AsNoTracking()
                .ToListAsync();

            var messages = absents
                .Where(a => !string.IsNullOrWhiteSpace(a.Student.Phone))
                .GroupBy(a => a.Student.Phone!)
                .Select(g => (
                    phone: g.Key!,
                    message: $"Sayın veli, öğrenciniz bazı ders(ler)e katılmamıştır. Son yoklama: {g.Max(x => x.Session.ScheduledAt):dd.MM.yyyy HH:mm}."
                ));

            await _smsService.SendBulkAsync(messages);
            TempData["Success"] = "SMS gönderimi başlatıldı.";
            return RedirectToAction(nameof(Index));
        }
    }
}


