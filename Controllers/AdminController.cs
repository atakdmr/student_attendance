using System;
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
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private readonly IUserService _userService;
        private readonly AppDbContext _db;
        private readonly ILessonConflictService _conflictService;

        public AdminController(IUserService userService, AppDbContext db, ILessonConflictService conflictService)
        {
            _userService = userService;
            _db = db;
            _conflictService = conflictService;
        }

        // Dashboard
        public async Task<IActionResult> Index()
        {
            var stats = new
            {
                TotalUsers = await _db.Users.CountAsync(),
                ActiveUsers = await _db.Users.CountAsync(u => u.IsActive),
                TotalStudents = await _db.Students.CountAsync(),
                ActiveStudents = await _db.Students.CountAsync(s => s.IsActive),
                TotalGroups = await _db.Groups.CountAsync(),
                TotalLessons = await _db.Lessons.CountAsync(l => l.IsActive)
            };

            ViewBag.Stats = stats;
            return View();
        }

        #region User Management

        public async Task<IActionResult> Users()
        {
            var users = await _userService.GetAllUsersAsync();
            var vm = new UserListVm
            {
                Users = users.Select(u => new UserListItemVm
                {
                    Id = u.Id,
                    UserName = u.UserName,
                    FullName = u.FullName,
                    Role = u.Role,
                    IsActive = u.IsActive
                }).OrderByDescending(r => r.Role == UserRole.Admin).ToList(),
                AdminCount = users.Count(u => u.Role == UserRole.Admin && u.IsActive)
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> CreateUser()
        {
            var availableLessons = await _db.Lessons
                .Where(l => l.IsActive)
                .Include(l => l.Group)
                .GroupBy(l => l.Title)
                .Select(g => new LessonSelectVm
                {
                    Id = g.First().Id,
                    Title = g.Key,
                    GroupName = g.First().Group.Name
                })
                .OrderBy(l => l.Title)
                .ToListAsync();

            var vm = new UserCreateEditVm
            {
                AvailableLessons = availableLessons
            };
            return View("EditUser", vm);
        }

        [HttpGet]
        public async Task<IActionResult> EditUser(Guid id)
        {
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
            {
                TempData["Error"] = "Kullanıcı bulunamadı.";
                return RedirectToAction(nameof(Users));
            }

            var availableLessons = await _db.Lessons
                .Where(l => l.IsActive)
                .Include(l => l.Group)
                .GroupBy(l => l.Title)
                .Select(g => new LessonSelectVm
                {
                    Id = g.First().Id,
                    Title = g.Key,
                    GroupName = g.First().Group.Name
                })
                .OrderBy(l => l.Title)
                .ToListAsync();

            // Get the lesson types (titles) that this user teaches, not the specific lesson instances
            var assignedLessonTitles = await _db.Lessons
                .Where(l => l.TeacherId == id && l.IsActive)
                .Select(l => l.Title)
                .Distinct()
                .ToListAsync();

            // Map lesson titles back to lesson IDs for the checkbox selection
            var assignedLessonIds = availableLessons
                .Where(l => assignedLessonTitles.Contains(l.Title))
                .Select(l => l.Id)
                .ToList();

            var vm = new UserCreateEditVm
            {
                Id = user.Id,
                UserName = user.UserName,
                FullName = user.FullName,
                Role = user.Role,
                IsActive = user.IsActive,
                AvailableLessons = availableLessons,
                AssignedLessonIds = assignedLessonIds
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveUser(UserCreateEditVm vm)
        {
            if (!ModelState.IsValid)
            {
                // Reload lessons for the view
                var availableLessons = await _db.Lessons
                    .Where(l => l.IsActive)
                    .Include(l => l.Group)
                    .GroupBy(l => l.Title)
                    .Select(g => new LessonSelectVm
                    {
                        Id = g.First().Id,
                        Title = g.Key,
                        GroupName = g.First().Group.Name
                    })
                    .OrderBy(l => l.Title)
                    .ToListAsync();
                vm.AvailableLessons = availableLessons;
                return View("EditUser", vm);
            }

            try
            {
                Guid userId;
                if (vm.IsEdit)
                {
                    // Update existing user
                    var user = await _userService.UpdateUserAsync(vm.Id!.Value, vm.UserName, vm.FullName, vm.Role, vm.IsActive);
                    if (user == null)
                    {
                        TempData["Error"] = "Kullanıcı bulunamadı.";
                        return RedirectToAction(nameof(Users));
                    }

                    // Change password if provided
                    if (!string.IsNullOrWhiteSpace(vm.Password))
                    {
                        await _userService.ChangePasswordAsync(vm.Id.Value, vm.Password);
                    }

                    userId = vm.Id.Value;
                    TempData["Success"] = "Kullanıcı başarıyla güncellendi.";
                }
                else
                {
                    // Create new user  
                    if (string.IsNullOrWhiteSpace(vm.Password))
                    {
                        ModelState.AddModelError("Password", "Yeni kullanıcı için şifre gereklidir.");
                        return View("EditUser", vm);
                    }

                    var newUser = await _userService.CreateUserAsync(vm.UserName, vm.FullName, vm.Password, vm.Role);
                    userId = newUser.Id;
                    TempData["Success"] = "Kullanıcı başarıyla oluşturuldu.";
                }

                // Update lesson assignments - allow multiple teachers for same lesson
                var currentAssigned = await _db.Lessons
                    .Where(l => l.TeacherId == userId && l.IsActive)
                    .ToListAsync();

                // Lessons to add (new assignments)
                var toAdd = vm.AssignedLessonIds.Where(id => !currentAssigned.Any(l => l.Id == id)).ToList();
                
                // Lessons to remove (unassigned) - remove the user's specific lesson instances
                var toRemove = currentAssigned.Where(l => !vm.AssignedLessonIds.Contains(l.Id)).ToList();

                // Add new lesson assignments - create new lesson instances for this teacher
                foreach (var lessonId in toAdd)
                {
                    var originalLesson = await _db.Lessons.FindAsync(lessonId);
                    if (originalLesson != null)
                    {
                        // Check for conflicts before creating the lesson
                        var conflict = await _conflictService.CheckConflictAsync(userId, originalLesson.GroupId, originalLesson.DayOfWeek, originalLesson.StartTime, originalLesson.EndTime);
                        if (conflict.HasConflict)
                        {
                            TempData["ConflictError"] = $"Öğretmen ataması yapılamadı: {conflict.Message}";
                            return RedirectToAction(nameof(Users));
                        }

                        // Create a new lesson instance for this teacher (same lesson, different teacher)
                        var newLesson = new Lesson
                        {
                            Title = originalLesson.Title,
                            DayOfWeek = originalLesson.DayOfWeek,
                            StartTime = originalLesson.StartTime,
                            EndTime = originalLesson.EndTime,
                            GroupId = originalLesson.GroupId,
                            TeacherId = userId,
                            IsActive = true
                        };
                        _db.Lessons.Add(newLesson);
                    }
                }

                // Remove lesson assignments - delete the user's specific lesson instances
                foreach (var lesson in toRemove)
                {
                    _db.Lessons.Remove(lesson);
                }

                await _db.SaveChangesAsync();

                return RedirectToAction(nameof(Users));
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                // Reload lessons for the view
                var availableLessons = await _db.Lessons
                    .Where(l => l.IsActive)
                    .Include(l => l.Group)
                    .GroupBy(l => l.Title)
                    .Select(g => new LessonSelectVm
                    {
                        Id = g.First().Id,
                        Title = g.Key,
                        GroupName = g.First().Group.Name
                    })
                    .OrderBy(l => l.Title)
                    .ToListAsync();
                vm.AvailableLessons = availableLessons;
                return View("EditUser", vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            try
            {
                Guid? reassignTo = null;
                if (Request.HasFormContentType)
                {
                    var raw = Request.Form["reassignTo"].ToString();
                    if (Guid.TryParse(raw, out var parsed) && parsed != Guid.Empty)
                    {
                        reassignTo = parsed;
                    }
                }

                var success = await _userService.DeleteUserAsync(id, reassignTo);
                if (success)
                {
                    TempData["Success"] = "Kullanıcı başarıyla silindi.";
                }
                else
                {
                    TempData["Error"] = "Kullanıcı bulunamadı.";
                }
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Users));
        }

        #endregion

        #region Student Management

        public async Task<IActionResult> Students()
        {
            // Tüm öğrencileri yükle (filtreleme ve pagination client-side yapılacak)
            var students = await _db.Students
                .Include(s => s.Group)
                .OrderBy(s => s.Group.Name)
                .ThenBy(s => s.LastName)
                .ThenBy(s => s.FirstName)
                .ToListAsync();

            var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();

            // Toplam istatistikler
            var totalStudents = students.Count;
            var activeStudents = students.Count(s => s.IsActive);
            var inactiveStudents = totalStudents - activeStudents;

            var vm = new StudentListVm
            {
                Students = students.Select(s => new StudentListItemVm
                {
                    StudentId = s.Id,
                    GroupId = s.GroupId,
                    FullName = s.FullName,
                    StudentNumber = s.StudentNumber,
                    IsActive = s.IsActive,
                    GroupName = s.Group?.Name ?? ""
                }).ToList(),
                Groups = groups.Select(g => new GroupSelectItemVm
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code
                }).ToList(),
                TotalStudentsCount = totalStudents,
                ActiveStudentsCount = activeStudents,
                InactiveStudentsCount = inactiveStudents
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> CreateStudent()
        {
            var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
            var vm = new StudentCreateEditVm
            {
                Groups = groups.Select(g => new GroupSelectItemVm
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code
                }).ToList()
            };

            return View("EditStudent", vm);
        }

        [HttpGet]
        public async Task<IActionResult> EditStudent(Guid id)
        {
            var student = await _userService.GetStudentByIdAsync(id);
            if (student == null)
            {
                TempData["Error"] = "Öğrenci bulunamadı.";
                return RedirectToAction(nameof(Students));
            }

            var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
            var vm = new StudentCreateEditVm
            {
                Id = student.Id,
                FirstName = student.FirstName,
                LastName = student.LastName,
                StudentNumber = student.StudentNumber,
                Phone = student.Phone,
                GroupId = student.GroupId,
                IsActive = student.IsActive,
                Groups = groups.Select(g => new GroupSelectItemVm
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code
                }).ToList()
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveStudent(StudentCreateEditVm vm)
        {
            if (!ModelState.IsValid)
            {
                // Reload groups for dropdown
                var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
                vm.Groups = groups.Select(g => new GroupSelectItemVm
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code
                }).ToList();

                return View("EditStudent", vm);
            }

            try
            {
                if (vm.IsEdit)
                {
                    // Update existing student
                    var student = await _userService.UpdateStudentAsync(vm.Id!.Value, vm.FirstName, vm.LastName, vm.StudentNumber, vm.Phone, vm.GroupId, vm.IsActive);
                    if (student == null)
                    {
                        TempData["Error"] = "Öğrenci bulunamadı.";
                        return RedirectToAction(nameof(Students));
                    }

                    TempData["Success"] = "Öğrenci başarıyla güncellendi.";
                }
                else
                {
                    // Create new student
                    await _userService.CreateStudentAsync(vm.FirstName, vm.LastName, vm.StudentNumber, vm.Phone, vm.GroupId);
                    TempData["Success"] = "Öğrenci başarıyla oluşturuldu.";
                }

                return RedirectToAction(nameof(Students));
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);

                // Reload groups for dropdown
                var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync();
                vm.Groups = groups.Select(g => new GroupSelectItemVm
                {
                    Id = g.Id,
                    Name = g.Name,
                    Code = g.Code
                }).ToList();

                return View("EditStudent", vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteStudent(Guid id)
        {
            try
            {
                var success = await _userService.DeleteStudentAsync(id);
                if (success)
                {
                    TempData["Success"] = "Öğrenci başarıyla silindi.";
                }
                else
                {
                    TempData["Error"] = "Öğrenci bulunamadı.";
                }
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Students));
        }

        #endregion
    }
}
