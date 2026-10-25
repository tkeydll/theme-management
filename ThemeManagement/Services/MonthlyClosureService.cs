using Microsoft.EntityFrameworkCore;
using ThemeManagement.Data;
using ThemeManagement.Domain.Entities;

namespace ThemeManagement.Services;

public interface IMonthlyClosureService
{
    Task<bool> IsClosedAsync(int year, int month);
    Task<HashSet<(int Year, int Month)>> GetClosedMonthsAsync(IEnumerable<(int Year, int Month)> months);
    Task SetClosedAsync(int year, int month, bool isClosed);
    Task<List<MonthlyClosure>> GetAllAsync();
}

public class MonthlyClosureService : IMonthlyClosureService
{
    private readonly AppDbContext _db;

    public MonthlyClosureService(AppDbContext db)
    {
        _db = db;
    }

    public Task<bool> IsClosedAsync(int year, int month)
    {
        return _db.MonthlyClosures.AsNoTracking().AnyAsync(x => x.Year == year && x.Month == month);
    }

    public async Task<HashSet<(int Year, int Month)>> GetClosedMonthsAsync(IEnumerable<(int Year, int Month)> months)
    {
        var monthList = months.Distinct().ToList();
        if (monthList.Count == 0)
            return [];

        var years = monthList.Select(x => x.Year).Distinct().ToList();
        var closures = await _db.MonthlyClosures
            .AsNoTracking()
            .Where(x => years.Contains(x.Year))
            .ToListAsync();

        var targetSet = monthList.ToHashSet();
        return closures
            .Select(x => (x.Year, x.Month))
            .Where(targetSet.Contains)
            .ToHashSet();
    }

    public async Task SetClosedAsync(int year, int month, bool isClosed)
    {
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "月は1〜12で指定してください");

        var existing = await _db.MonthlyClosures.FirstOrDefaultAsync(x => x.Year == year && x.Month == month);
        if (isClosed)
        {
            if (existing == null)
            {
                _db.MonthlyClosures.Add(new MonthlyClosure
                {
                    Year = year,
                    Month = month,
                    ClosedAt = DateTime.Now
                });
                await _db.SaveChangesAsync();
            }
            return;
        }

        if (existing != null)
        {
            _db.MonthlyClosures.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    public Task<List<MonthlyClosure>> GetAllAsync()
    {
        return _db.MonthlyClosures
            .AsNoTracking()
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .ToListAsync();
    }
}
