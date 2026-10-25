using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using QuestPDF.Infrastructure;
using ThemeManagement.Components;
using ThemeManagement.Data;
using ThemeManagement.Infrastructure;
using ThemeManagement.Services;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MudBlazor
builder.Services.AddMudServices();

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Azure App Service Easy Auth — authentication is handled by Easy Auth at the infrastructure level.
// This handler reads the X-MS-CLIENT-PRINCIPAL header forwarded by App Service and constructs
// the ClaimsPrincipal, including Entra ID App Role claims used for authorization below.
//
// In local development, set DevAuth:Enabled = true in appsettings.Development.json to bypass
// Easy Auth and auto-authenticate with a local dev identity (see DevBypassAuthHandler).
var devAuthEnabled = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DevAuth:Enabled");
var authBuilder = builder.Services.AddAuthentication("EasyAuth");
if (devAuthEnabled)
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevBypassAuthHandler>("EasyAuth", null);
else
    authBuilder.AddScheme<AuthenticationSchemeOptions, EasyAuthAuthenticationHandler>("EasyAuth", null);

builder.Services.AddAuthorization();

builder.Services.AddCascadingAuthenticationState();

// Application services
builder.Services.AddScoped<IGradeService, GradeService>();
builder.Services.AddScoped<IEngineerService, EngineerService>();
builder.Services.AddScoped<IWorkDayService, WorkDayService>();
builder.Services.AddScoped<IThemeService, ThemeService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAllocationService, AllocationService>();
builder.Services.AddScoped<IMonthlyClosureService, MonthlyClosureService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IDashboardSettingsService, DashboardSettingsService>();
builder.Services.AddScoped<ISkillService, SkillService>();
builder.Services.AddSingleton<IJapaneseBusinessDayService, JapaneseBusinessDayService>();
builder.Services.AddSingleton<ICapacitySettings, CapacitySettings>();
builder.Services.AddSingleton<ICopilotAgentSettings, CopilotAgentSettings>();
builder.Services.AddScoped<IReportPdfService, ReportPdfService>();
builder.Services.AddSingleton<ICopilotAgentService, CopilotAgentService>();

var app = builder.Build();

// Apply migrations automatically on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// DB バックアップダウンロード
app.MapGet("/api/backup/download", async (IConfiguration config) =>
{
    var connStr = config.GetConnectionString("DefaultConnection") ?? "Data Source=theme_management.db";
    // SQLiteのパスを抽出
    var sqliteConnectionStringBuilder = new SqliteConnectionStringBuilder(connStr);
    var dataSource = string.IsNullOrWhiteSpace(sqliteConnectionStringBuilder.DataSource)
        ? "theme_management.db"
        : sqliteConnectionStringBuilder.DataSource;

    if (!File.Exists(dataSource))
        return Results.NotFound("DBファイルが見つかりません");

    var tempFile = Path.Combine(
        Path.GetTempPath(),
        $"theme_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.db");
    try
    {
        // VACUUM INTO で安全にコピー（WALのコミット済みデータを含む）
        using var conn = new SqliteConnection($"Data Source={dataSource}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        var escapedTempFile = tempFile.Replace("'", "''");
        cmd.CommandText = $"VACUUM INTO '{escapedTempFile}'";
        await cmd.ExecuteNonQueryAsync();

        var fileName = $"theme_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
        var bytes = await File.ReadAllBytesAsync(tempFile);
        File.Delete(tempFile);

        return Results.File(bytes, "application/octet-stream", fileName);
    }
    catch
    {
        if (File.Exists(tempFile))
            File.Delete(tempFile);

        throw;
    }
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ===== PDF レポートエンドポイント =====
app.MapGet("/api/report/pdf", async (
    string type,
    IReportPdfService pdfService,
    int? year,
    int? month,
    int? fiscalYear,
    string? half) =>
{
    try
    {
        byte[] pdfBytes;
        string fileName;

        if (type == "dashboard")
        {
            var y = year ?? DateTime.Today.Year;
            var m = month ?? DateTime.Today.Month;
            pdfBytes = await pdfService.GenerateDashboardPdfAsync(y, m);
            fileName = $"dashboard_{y}{m:D2}.pdf";
        }
        else if (type == "forecast")
        {
            var fy = fiscalYear ?? (DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1);
            var isFirst = !(half == "second");
            pdfBytes = await pdfService.GenerateForecastPdfAsync(fy, isFirst);
            var halfLabel = isFirst ? "first" : "second";
            fileName = $"forecast_{fy}_{halfLabel}.pdf";
        }
        else
        {
            return Results.BadRequest("type パラメータには 'dashboard' または 'forecast' を指定してください。");
        }

        return Results.File(pdfBytes, "application/pdf", fileName);
    }
    catch (Exception ex)
    {
        return Results.Problem($"PDF 生成中にエラーが発生しました: {ex.Message}");
    }
})
.WithName("GetReportPdf")
.WithTags("Reports");

app.Run();
