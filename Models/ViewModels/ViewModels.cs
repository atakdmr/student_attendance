using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Yoklama.Models.Entities;

namespace Yoklama.Models.ViewModels
{
    // Sidebar
    public class SidebarVm
    {
        public List<GroupSidebarItem> Groups { get; set; } = new();
    }

    public class GroupSidebarItem
    {
        public Guid GroupId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<LessonSidebarItem> Lessons { get; set; } = new();
    }

    public class LessonSidebarItem
    {
        public Guid LessonId { get; set; }
        public string Title { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
    }

    // Group Details
    public class GroupDetailVm
    {
        public Guid GroupId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<LessonVm> Lessons { get; set; } = new();
        public List<StudentListItemVm> Students { get; set; } = new();
    }

    public class StudentListItemVm
    {
        public Guid StudentId { get; set; }
        public Guid GroupId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string StudentNumber { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string GroupName { get; set; } = string.Empty;
    }

    public class LessonVm
    {
        public Guid Id { get; set; }
        public Guid GroupId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int DayOfWeek { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public Guid TeacherId { get; set; }
        public string TeacherName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    // Attendance Session
    public class AttendanceSessionVm
    {
        public SessionInfoVm Session { get; set; } = new();
        public List<StudentAttendanceVm> Students { get; set; } = new();
    }

    public class SessionInfoVm
    {
        public Guid SessionId { get; set; }
        public Guid LessonId { get; set; }
        public Guid GroupId { get; set; }
        public Guid TeacherId { get; set; }
        public DateTimeOffset ScheduledAt { get; set; }
        public SessionStatus Status { get; set; }
        public string LessonTitle { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
    }

    public class StudentAttendanceVm
    {
        public Guid StudentId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string StudentNumber { get; set; } = string.Empty;
        public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
        public int? LateMinutes { get; set; }
        public string? Note { get; set; }
        public byte[]? RowVersion { get; set; }
    }

    public class BulkMarkVm
    {
        [Required]
        public Guid SessionId { get; set; }

        [MinLength(1)]
        public List<StudentAttendanceVm> Students { get; set; } = new();
    }

    // Create/Edit common
    public class CreateEditVm
    {
        public Guid? Id { get; set; }
        
        [Required(ErrorMessage = "Ders başlığı gereklidir.")]
        [StringLength(100, ErrorMessage = "Ders başlığı en fazla 100 karakter olabilir.")]
        public string Title { get; set; } = string.Empty;
        
        [Range(1, 7, ErrorMessage = "Geçerli bir gün seçiniz.")]
        public int DayOfWeek { get; set; }
        
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        
        [Required(ErrorMessage = "Grup seçimi gereklidir.")]
        public Guid GroupId { get; set; }
        
        [Required(ErrorMessage = "Öğretmen seçimi gereklidir.")]
        public Guid TeacherId { get; set; }
        
        public bool IsActive { get; set; } = true;
    }

    // Account/Login
    public class LoginVm
    {
        [Required]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
    }

    // Admin - User Management
    public class UserListVm
    {
        public List<UserListItemVm> Users { get; set; } = new();
        public int AdminCount { get; set; }
    }

    public class UserListItemVm
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public UserRole Role { get; set; }
        public bool IsActive { get; set; }
    }

    public class UserCreateEditVm
    {
        public Guid? Id { get; set; }

        [Required(ErrorMessage = "Kullanıcı adı gereklidir.")]
        [StringLength(50, ErrorMessage = "Kullanıcı adı en fazla 50 karakter olabilir.")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Ad Soyad gereklidir.")]
        [StringLength(100, ErrorMessage = "Ad Soyad en fazla 100 karakter olabilir.")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Rol seçimi gereklidir.")]
        public UserRole Role { get; set; } = UserRole.Teacher;

        public bool IsActive { get; set; } = true;

        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Şifre en az 6, en fazla 100 karakter olmalıdır.")]
        public string? Password { get; set; }

        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Şifreler eşleşmiyor.")]
        public string? ConfirmPassword { get; set; }

        public List<LessonSelectVm> AvailableLessons { get; set; } = new();
        public List<Guid> AssignedLessonIds { get; set; } = new();

        public bool IsEdit => Id.HasValue;
    }

    public class LessonSelectVm
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
    }

    // Admin - Student Management
    public class StudentListVm
    {
        public List<StudentListItemVm> Students { get; set; } = new();
        public List<GroupSelectItemVm> Groups { get; set; } = new();
        
        // İstatistikler
        public int TotalStudentsCount { get; set; }
        public int ActiveStudentsCount { get; set; }
        public int InactiveStudentsCount { get; set; }
    }

    public class StudentCreateEditVm
    {
        public Guid? Id { get; set; }

        [Required(ErrorMessage = "Ad gereklidir.")]
        [StringLength(50, ErrorMessage = "Ad en fazla 50 karakter olabilir.")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Soyad gereklidir.")]
        [StringLength(50, ErrorMessage = "Soyad en fazla 50 karakter olabilir.")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Öğrenci numarası gereklidir.")]
        [StringLength(20, ErrorMessage = "Öğrenci numarası en fazla 20 karakter olabilir.")]
        public string StudentNumber { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Geçerli bir telefon numarası girin.")]
        [StringLength(20, ErrorMessage = "Telefon en fazla 20 karakter olabilir.")]
        public string? Phone { get; set; }

        [Required(ErrorMessage = "Grup seçimi gereklidir.")]
        public Guid GroupId { get; set; }

        public bool IsActive { get; set; } = true;

        public List<GroupSelectItemVm> Groups { get; set; } = new();

        public bool IsEdit => Id.HasValue;
    }

    public class GroupSelectItemVm
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    // Reports
    public class StudentReportVm
    {
        public Student Student { get; set; } = default!;
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public List<AttendanceReportItemVm> AttendanceRecords { get; set; } = new();
        public AttendanceSummaryVm Summary { get; set; } = new();
    }

    public class GroupReportVm
    {
        public Group Group { get; set; } = default!;
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public List<StudentAttendanceSummaryVm> StudentSummaries { get; set; } = new();
    }

    public class AttendanceReportItemVm
    {
        public DateTimeOffset ScheduledAt { get; set; }
        public string LessonTitle { get; set; } = string.Empty;
        public AttendanceStatus Status { get; set; }
        public int? LateMinutes { get; set; }
        public string? Note { get; set; }
        public Guid SessionId { get; set; }
    }

    public class AttendanceSummaryVm
    {
        public int TotalSessions { get; set; }
        public int PresentCount { get; set; }
        public int AbsentCount { get; set; }
        public int LateCount { get; set; }
        public int ExcusedCount { get; set; }
        public double AttendanceRate => TotalSessions > 0 ? (double)PresentCount / TotalSessions * 100 : 0;
    }

    public class StudentAttendanceSummaryVm
    {
        public Student Student { get; set; } = default!;
        public AttendanceSummaryVm Summary { get; set; } = new();
    }


    // Account/Profile
    public class ProfileVm
    {
        [Required(ErrorMessage = "Kullanıcı adı gereklidir.")]
        [StringLength(50, ErrorMessage = "Kullanıcı adı en fazla 50 karakter olabilir.")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Ad Soyad gereklidir.")]
        [StringLength(100, ErrorMessage = "Ad Soyad en fazla 100 karakter olabilir.")]
        public string FullName { get; set; } = string.Empty;
    }

    public class ChangePasswordVm
    {
        [Required(ErrorMessage = "Mevcut şifre gereklidir.")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Yeni şifre gereklidir.")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Şifre en az 6 karakter olmalıdır.")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Şifre tekrarı gereklidir.")]
        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "Şifreler eşleşmiyor.")]
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }

    public class ActivityItemVm
    {
        public string Action { get; set; } = string.Empty;
        public string Entity { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public class ActivitySummaryVm
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Total => Created + Updated + Deleted;
        public DateTime? LastActivityAt { get; set; }
    }

    public class ProfilePageVm
    {
        public ProfileVm Profile { get; set; } = new();
        public ChangePasswordVm ChangePassword { get; set; } = new();
        public string Role { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<ActivityItemVm> RecentActivities { get; set; } = new();
        public ActivitySummaryVm ActivitySummary { get; set; } = new();
    }

    // Schedule/Index - Simple
    public class ScheduleVm
    {
        public List<ScheduleDayVm> Days { get; set; } = new();
        public List<Group> Groups { get; set; } = new();
        public bool IsAdmin { get; set; }
    }

    public class ScheduleDayVm
    {
        public int DayOfWeek { get; set; }
        public string DayName { get; set; } = string.Empty;
        public List<ScheduleLessonVm> Lessons { get; set; } = new();
    }

    public class ScheduleLessonVm
    {
        public Guid LessonId { get; set; }
        public string Title { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public string TeacherName { get; set; } = string.Empty;
        public Guid TeacherId { get; set; }
        public SessionStatus? SessionStatus { get; set; }
        public Guid? SessionId { get; set; }
        public bool IsActive { get; set; }
    }

    public class LessonsVm
    {
        public List<LessonWithSessionVm> Lessons { get; set; } = new();
        public bool IsAdmin { get; set; }
    }

    public class LessonWithSessionVm
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int DayOfWeek { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public bool IsActive { get; set; }
        public Group? Group { get; set; }
        public User? Teacher { get; set; }
        public Guid? SessionId { get; set; }
        public SessionStatus? SessionStatus { get; set; }
    }

}
