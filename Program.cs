using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yoklama.Data;

var builder = WebApplication.CreateBuilder(args);

﻿// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

// ✅ OPTIMIZATION: Output caching ekle
builder.Services.AddOutputCache(options =>
{
    // Base policy: Tüm sayfalar için cache aktif
    options.AddBasePolicy(builder => 
        builder.Expire(TimeSpan.FromMinutes(5))
               .Tag("page-cache"));
    
    // Dashboard için özel policy (daha kısa cache)
    options.AddPolicy("dashboard", builder => 
        builder.Expire(TimeSpan.FromMinutes(2))
               .Tag("dashboard-cache"));
    
    // Static data için daha uzun cache
    options.AddPolicy("static-data", builder => 
        builder.Expire(TimeSpan.FromMinutes(30))
               .Tag("static-cache"));
});

// ✅ OPTIMIZATION: Rate limiting ekle (DDoS koruması) - sadece production'da
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddRateLimiter(options =>
    {
        // Global fallback policy - IP bazlı rate limiting
        options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<Microsoft.AspNetCore.Http.HttpContext, string>(context =>
        {
            var userName = context.User.Identity?.Name;
            var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var partitionKey = userName ?? remoteIp;
            
            return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: partitionKey,
                factory: partition => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = 1000, // Dakikada 1000 istek (çok daha yüksek)
                    QueueLimit = 0,
                    Window = TimeSpan.FromMinutes(1)
                });
        });
        
        // Rate limit aşıldığında döndürülecek durum kodu
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    // Use an explicit server version to avoid design-time connection attempts during migrations
    var serverVersion = new MySqlServerVersion(new Version(8, 0, 36));
    options.UseMySql(connectionString, serverVersion);
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(24); // Cookie 24 saat geçerli
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = builder.Environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict;
        options.Cookie.IsEssential = true; // Cookie consent'e gerek yok
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.Redirect(options.LoginPath);
                return Task.CompletedTask;
            }
        };
    });

// Domain services
builder.Services.AddScoped<Yoklama.Services.IUserService, Yoklama.Services.AuthService>();
builder.Services.AddScoped<Yoklama.Services.IGroupService, Yoklama.Services.GroupService>();
builder.Services.AddScoped<Yoklama.Services.IAttendanceService, Yoklama.Services.AttendanceService>();
builder.Services.AddScoped<Yoklama.Services.ILessonConflictService, Yoklama.Services.LessonConflictService>();
builder.Services.AddHttpClient<Yoklama.Services.Sms.NetGsmSmsService>();
builder.Services.AddScoped<Yoklama.Services.Sms.ISmsService, Yoklama.Services.Sms.NetGsmSmsService>();

var app = builder.Build();

// Apply migrations and seed initial data (skippable for design-time tooling)
if (!string.Equals(Environment.GetEnvironmentVariable("DISABLE_EF_STARTUP"), "true", StringComparison.OrdinalIgnoreCase))
{
    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
        try
        {
            Yoklama.Data.SeedData.EnsureSeedAsync(services).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred while seeding the database.");
        }
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    app.UseHttpsRedirection();
}

// ✅ OPTIMIZATION: Sadece HTML sayfalar için cache kapatıldı, static dosyalar için korundu
app.Use(async (context, next) =>
{
    // Static asset path'leri için cache açık bırak
    var path = context.Request.Path.Value?.ToLowerInvariant();
    var isStaticAsset = path != null && (
        path.StartsWith("/css") ||
        path.StartsWith("/js") ||
        path.StartsWith("/lib") ||
        path.StartsWith("/images") ||
        path.EndsWith(".css") ||
        path.EndsWith(".js") ||
        path.EndsWith(".jpg") ||
        path.EndsWith(".jpeg") ||
        path.EndsWith(".png") ||
        path.EndsWith(".gif") ||
        path.EndsWith(".svg") ||
        path.EndsWith(".woff") ||
        path.EndsWith(".woff2") ||
        path.EndsWith(".ttf") ||
        path.EndsWith(".ico")
    );

    if (!isStaticAsset)
    {
        // Sadece HTML sayfalar için cache kapatılsın
        context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        context.Response.Headers.Append("Pragma", "no-cache");
        context.Response.Headers.Append("Expires", "0");
    }
    
    // Güvenlik header'ları tüm response'lar için
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    
    await next();
});

// ✅ OPTIMIZATION: Static files için cache headers ayarlandı (Authentication'dan ÖNCE)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // 1 yıl (31536000 saniye) cache süresi
        if (!app.Environment.IsDevelopment())
        {
            ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=31536000,immutable");
        }
        else
        {
            // Development'ta daha kısa cache
            ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=3600");
        }
    }
});

app.UseRouting();

// ✅ OPTIMIZATION: Rate limiter middleware (sadece production'da)
if (!app.Environment.IsDevelopment())
{
    app.UseRateLimiter();
}

// ✅ OPTIMIZATION: Output caching middleware
app.UseOutputCache();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
