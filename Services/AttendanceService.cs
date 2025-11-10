using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Yoklama.Data;
using Yoklama.Models.Entities;
using Yoklama.Models.ViewModels;

namespace Yoklama.Services
{
    public class AttendanceService : IAttendanceService
    {
        private readonly AppDbContext _db;

        public AttendanceService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<AttendanceSession> CreateNewSessionAsync(Guid lessonId, DateTime sessionDate, Guid currentUserId)
        {
            var lesson = await _db.Lessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId && l.IsActive);
            if (lesson == null)
                throw new InvalidOperationException("Ders bulunamadı veya aktif değil.");

            var sessionDateTime = sessionDate.Date.Add(lesson.StartTime);
            var session = new AttendanceSession
            {
                LessonId = lesson.Id,
                GroupId = lesson.GroupId,
                TeacherId = lesson.TeacherId,
                ScheduledAt = sessionDateTime,
                Status = SessionStatus.Open,
                CreatedAt = DateTime.UtcNow
            };

            await _db.AttendanceSessions.AddAsync(session);
            await _db.SaveChangesAsync();

            return session;
        }

        public async Task<AttendanceSession?> GetExistingSessionAsync(Guid lessonId, DateTime sessionDate)
        {
            var startOfDay = sessionDate.Date;
            var endOfDay = sessionDate.Date.AddDays(1);
            
            return await _db.AttendanceSessions
                .Where(s => s.LessonId == lessonId 
                         && s.ScheduledAt >= startOfDay 
                         && s.ScheduledAt < endOfDay)
                .FirstOrDefaultAsync();
        }

        // Haftalık session kontrolü için yeni method
        public async Task<AttendanceSession?> GetWeeklySessionAsync(Guid lessonId, DateTime sessionDate)
        {
            // Önce tam tarih eşleşmesi ara
            var exactMatch = await GetExistingSessionAsync(lessonId, sessionDate);
            if (exactMatch != null)
                return exactMatch;

            // Haftalık ders için, aynı haftanın aynı gününü ara
            var lesson = await _db.Lessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId);
            if (lesson == null)
                return null;

            // ✅ OPTIMIZATION: MySQL destekli direkt tarih filtreleme (client-side kaldırıldı)
            var targetDayOfWeek = (int)sessionDate.DayOfWeek;
            if (targetDayOfWeek == 0) targetDayOfWeek = 7; // Pazar = 7

            // Aynı hafta içinde aynı günün session'ını ara
            var weekStart = sessionDate.Date.AddDays(-(int)sessionDate.DayOfWeek + 1);
            var weekEnd = weekStart.AddDays(7);
            
            return await _db.AttendanceSessions
                .Where(s => s.LessonId == lessonId 
                         && s.Status != SessionStatus.Finalized
                         && s.ScheduledAt >= weekStart 
                         && s.ScheduledAt < weekEnd)
                .OrderByDescending(s => s.ScheduledAt)
                .FirstOrDefaultAsync();
        }

        public async Task<AttendanceSession> OpenOrGetSessionAsync(Guid lessonId, DateTimeOffset scheduledAt, Guid currentUserId)
        {
            // Önce mevcut session'ı kontrol et (haftalık dahil)
            var existingSession = await GetWeeklySessionAsync(lessonId, scheduledAt.Date);
            if (existingSession != null)
            {
                return existingSession;
            }
            
            // Mevcut session yoksa yeni oluştur
            return await CreateNewSessionAsync(lessonId, scheduledAt.Date, currentUserId);
        }

        public async Task<AttendanceSessionVm?> GetSessionVmAsync(Guid sessionId)
        {
            var session = await _db.AttendanceSessions
                .AsNoTracking()
                .Include(s => s.Lesson)
                .Include(s => s.Group)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
                return null;

            // students in group
            var students = await _db.Students
                .AsNoTracking()
                .Where(st => st.GroupId == session.GroupId && st.IsActive)
                .OrderBy(st => st.LastName).ThenBy(st => st.FirstName)
                .ToListAsync();

            // existing records for this session
            var records = await _db.AttendanceRecords
                .AsNoTracking()
                .Where(r => r.SessionId == session.Id)
                .ToListAsync();

            var vm = new AttendanceSessionVm
            {
                Session = new SessionInfoVm
                {
                    SessionId = session.Id,
                    LessonId = session.LessonId,
                    GroupId = session.GroupId,
                    TeacherId = session.TeacherId,
                    ScheduledAt = session.ScheduledAt,
                    Status = session.Status,
                    LessonTitle = session.Lesson.Title,
                    GroupName = session.Group.Name
                },
                Students = students.Select(st =>
                {
                    var rec = records.FirstOrDefault(r => r.StudentId == st.Id);
                    return new StudentAttendanceVm
                    {
                        StudentId = st.Id,
                        FullName = st.FullName,
                        StudentNumber = st.StudentNumber,
                        Status = rec?.Status ?? AttendanceStatus.Present,
                        LateMinutes = rec?.LateMinutes,
                        Note = rec?.Note,
                        RowVersion = rec?.RowVersion
                    };
                }).ToList()
            };

            return vm;
        }

        public async Task BulkMarkAsync(BulkMarkVm vm, Guid markedByUserId)
        {
            // Load session with status
            var session = await _db.AttendanceSessions.FirstOrDefaultAsync(s => s.Id == vm.SessionId);
            if (session == null) throw new InvalidOperationException("Oturum bulunamadı.");
            if (session.Status == SessionStatus.Finalized) throw new UnauthorizedAccessException("Oturum kapatılmış, yoklama alınamaz.");

            using var tx = await _db.Database.BeginTransactionAsync();

            try
            {
                foreach (var dto in vm.Students)
                {
                    await UpsertRecordAsync(session, dto, markedByUserId);
                }

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task MarkAsync(Guid sessionId, StudentAttendanceVm dto, Guid markedByUserId)
        {
            var session = await _db.AttendanceSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) throw new InvalidOperationException("Oturum bulunamadı.");
            if (session.Status == SessionStatus.Finalized) throw new UnauthorizedAccessException("Oturum kapatılmış, yoklama alınamaz.");

            await UpsertRecordAsync(session, dto, markedByUserId);
            await _db.SaveChangesAsync();
        }

        public async Task FinalizeSessionAsync(Guid sessionId, Guid currentUserId)
        {
            var session = await _db.AttendanceSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) throw new InvalidOperationException("Oturum bulunamadı.");
            if (session.Status == SessionStatus.Finalized) throw new InvalidOperationException("Oturum zaten kapatılmış.");

            session.Status = SessionStatus.Finalized;
            session.EndTime = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task<IEnumerable<AttendanceSession>> GetSessionsForTeacherAsync(Guid teacherId)
        {
            return await _db.AttendanceSessions
                .AsNoTracking()
                .Include(s => s.Lesson)
                .Include(s => s.Group)
                .Where(s => s.TeacherId == teacherId && s.Status != SessionStatus.Finalized)
                .OrderByDescending(s => s.Id)
                .ToListAsync();
        }

        // Haftalık session'ın tarihini güncelle
        public async Task<AttendanceSession> UpdateSessionForNewWeekAsync(Guid sessionId, DateTime newDate, Guid currentUserId)
        {
            var session = await _db.AttendanceSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
                throw new InvalidOperationException("Oturum bulunamadı.");

            // Yeni tarihi hesapla
            var lesson = await _db.Lessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == session.LessonId);
            if (lesson == null)
                throw new InvalidOperationException("Ders bulunamadı.");

            var newScheduledAt = newDate.Date.Add(lesson.StartTime);
            session.ScheduledAt = newScheduledAt;
            session.Status = SessionStatus.Open; // Yeni hafta için oturumu aç
            session.EndTime = null; // End time'ı sıfırla

            // Eski attendance record'ları sil (yeni hafta için temiz başlangıç)
            var oldRecords = await _db.AttendanceRecords
                .Where(r => r.SessionId == sessionId)
                .ToListAsync();
            
            _db.AttendanceRecords.RemoveRange(oldRecords);

            await _db.SaveChangesAsync();
            return session;
        }

        private async Task UpsertRecordAsync(AttendanceSession session, StudentAttendanceVm dto, Guid markedByUserId)
        {
            // validate student belongs to group
            var belongs = await _db.Students.AnyAsync(st => st.Id == dto.StudentId && st.GroupId == session.GroupId);
            if (!belongs) throw new InvalidOperationException("Öğrenci bu oturumun grubuna ait değil.");

            var existing = await _db.AttendanceRecords
                .FirstOrDefaultAsync(r => r.SessionId == session.Id && r.StudentId == dto.StudentId);

            if (existing == null)
            {
                var rec = new AttendanceRecord
                {
                    SessionId = session.Id,
                    StudentId = dto.StudentId,
                    Status = dto.Status,
                    LateMinutes = dto.LateMinutes,
                    Note = dto.Note,
                    MarkedAt = DateTimeOffset.UtcNow,
                    MarkedBy = markedByUserId
                };
                await _db.AttendanceRecords.AddAsync(rec);
            }
            else
            {
                // optimistic concurrency with RowVersion
                if (dto.RowVersion != null)
                {
                    _db.Entry(existing).Property(nameof(AttendanceRecord.RowVersion)).OriginalValue = dto.RowVersion;
                }

                existing.Status = dto.Status;
                existing.LateMinutes = dto.LateMinutes;
                existing.Note = dto.Note;
                existing.MarkedAt = DateTimeOffset.UtcNow;
                existing.MarkedBy = markedByUserId;

                _db.AttendanceRecords.Update(existing);
            }
        }
    }
}
