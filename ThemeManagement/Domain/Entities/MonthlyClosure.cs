namespace ThemeManagement.Domain.Entities;

/// <summary>月次の実績入力締め情報</summary>
public class MonthlyClosure
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTime ClosedAt { get; set; }
}
