using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ThemeManagement.Data;
using ThemeManagement.Domain.Entities;

namespace ThemeManagement.Services;

/// <summary>
/// Azure OpenAI または GitHub Copilot SDK でテーマへのエンジニアアサインを支援します。
/// </summary>
public interface ICopilotAgentService
{
    Task<CopilotAgentSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync();
}

public class CopilotAgentService : ICopilotAgentService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICapacitySettings _capacitySettings;
    private readonly ICopilotAgentSettings _agentSettings;
    private readonly ILogger<CopilotAgentService> _logger;

    internal const string AzureEndpointVariable = "AZURE_OPENAI_ENDPOINT";
    internal const string TokenScopeVariable = "AZURE_OPENAI_TOKEN_SCOPE";

    internal static string AzureEndpoint => GetRequiredEnvironmentVariable(AzureEndpointVariable);
    internal static string TokenScope => GetRequiredEnvironmentVariable(TokenScopeVariable);

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"環境変数 {name} が設定されていません");
        return value;
    }

    private string SystemPrompt =>
        string.IsNullOrWhiteSpace(_agentSettings.CustomSystemPrompt)
            ? DefaultSystemPrompt
            : _agentSettings.CustomSystemPrompt;

    public static string DefaultSystemPrompt => $"""
        あなたは「テーマ稼働予測システム」のアシスタントです。
        このシステムは「どのテーマ（案件）に誰をどれだけ投入すれば、予定通りに終わるか」「エンジニアの稼働が多すぎたり少なすぎたりしないか」を把握するためのものです。

        【重要】稼働データの解釈:
        - 今月（{DateTime.Now:yyyy年M月}）以降のエンジニア比長・割り当てデータはすべて「予測」です。実績ではなく、今後の計画を示します。
        - 過去の割り当て（今月未満）は実績です。
        - 「予測」を変更することで、終了時期や稼働バランスをシミュレートできます。

        利用可能なツール:
        - get_active_themes: アクティブなテーマ（案件）の一覧を取得します
        - get_active_engineers: アクティブなエンジニアの一覧を取得します
        - get_engineer_availability: 特定の年月におけるエンジニアの稼働余力を取得します
        - get_all_engineers_availability: 特定の年月における全エンジニアの稼働余力を一覧で取得します
        - get_theme_allocations: テーマの既存割り当て状況を取得します
        - assign_engineer_to_theme: エンジニアをテーマに割り当てます（予測値の設定）

        アサイン・予測提案時の考慮事項:
        1. エンジニアの残余稼働時間（余力）を確認する
        2. テーマの必要スキルとエンジニアのスキルをマッチングする
        3. スキルレベル（エンジニアのレベル >= テーマの要求レベル）を確認する
        4. テーマの受注金額上限と割り当てコストを確認する（ただし、OrderType が「その他」のテーマではこの受注金額上限チェックは不要）
        5. テーマの完了予定日までに必要工数がまかなえるかを確認する

        テーマのコスト・時間集計に関する注意事項:
        - get_theme_allocations で取得したデータにはクローズ済み（status が "完了" または "中止" 等のアクティブでない）テーマへの割り当ても含まれる場合がある
        - 稼働余力や残予算を計算する際は、アクティブなテーマ（status が "進行中" 等の稼働中のもの）への割り当て時間のみを集計すること
        - クローズ済みテーマへの割り当て時間はエンジニアの現在の稼働余力の計算に含めないこと

        ユーザーがアサインを依頼したら、最適なエンジニアを提案し、確認後に assign_engineer_to_theme を実行してください。
        """;

    public CopilotAgentService(
        IServiceScopeFactory scopeFactory,
        ICapacitySettings capacitySettings,
        ICopilotAgentSettings agentSettings,
        ILogger<CopilotAgentService> logger)
    {
        _scopeFactory = scopeFactory;
        _capacitySettings = capacitySettings;
        _agentSettings = agentSettings;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            if (_agentSettings.Provider == "CopilotSdk")
            {
                await using var client = new CopilotClient();
                await client.StartAsync();
                await client.ListModelsAsync();
                return true;
            }

            if (_agentSettings.Provider != "AzureOpenAI")
                throw new InvalidOperationException($"未対応のエージェントプロバイダーです: {_agentSettings.Provider}");

            var credential = new AzureApiCredential();
            await credential.GetTokenAsync(new TokenRequestContext([TokenScope]), default);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "エージェントプロバイダー {Provider} の利用可能性確認に失敗しました", _agentSettings.Provider);
            return false;
        }
    }

    public async Task<CopilotAgentSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var tools = BuildTools();
        if (_agentSettings.Provider == "CopilotSdk")
        {
            var client = new CopilotClient();
            try
            {
                await client.StartAsync();
                var session = await client.CreateSessionAsync(new SessionConfig
                {
                    Model = _agentSettings.Model,
                    SystemMessage = new SystemMessageConfig { Content = SystemPrompt },
                    OnPermissionRequest = PermissionHandler.ApproveAll,
                    Tools = tools,
                    AvailableTools = tools.Select(tool => $"custom:{tool.Name}").ToList(),
                });
                _logger.LogInformation("GitHub Copilot SDK セッション作成: model={Model}", _agentSettings.Model);
                return new CopilotAgentSession(client, session, _logger);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        if (_agentSettings.Provider != "AzureOpenAI")
            throw new InvalidOperationException($"未対応のエージェントプロバイダーです: {_agentSettings.Provider}");

        var credential = new AzureApiCredential();
        var azureClient = new AzureOpenAIClient(new Uri(AzureEndpoint), credential);
        var chatClient = azureClient
            .GetChatClient(_agentSettings.Model)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        _logger.LogInformation("Azure OpenAI セッション作成: model={Model}", _agentSettings.Model);
        return new CopilotAgentSession(chatClient, tools, SystemPrompt, _logger);
    }

    private static AIFunctionFactoryOptions MakeTool(string name, string description) =>
        new AIFunctionFactoryOptions { Name = name, Description = description };

    private AIFunction[] BuildTools()
    {
        return
        [
            // ─── テーマ一覧取得 ───
            AIFunctionFactory.Create(
                async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var themes = await db.Themes
                        .AsNoTracking()
                        .Include(t => t.RequiredSkills).ThenInclude(rs => rs.Skill)
                        .Where(t => ThemeStatus.ActiveStatuses.Contains(t.Status))
                        .OrderBy(t => t.Name)
                        .ToListAsync();

                    return themes.Select(t => new
                    {
                        id = t.Id,
                        themeNo = t.ThemeNo,
                        name = t.Name,
                        status = t.Status,
                        orderType = t.OrderType,
                        orderAmount = t.OrderAmount,
                        estimatedCompletionDate = t.EstimatedCompletionDate.ToString("yyyy-MM-dd"),
                        requiredSkills = t.RequiredSkills.Select(rs => new
                        {
                            skillId = rs.SkillId,
                            skillName = rs.Skill.Name,
                            requiredLevel = rs.RequiredLevel,
                        }).ToList(),
                    }).ToList();
                },
                MakeTool("get_active_themes",
                    "アクティブなテーマ（案件）の一覧を取得します。テーマID、テーマ名、受注金額、必要スキルなどが含まれます。")),

            // ─── エンジニア一覧取得 ───
            AIFunctionFactory.Create(
                async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var engineers = await db.Engineers
                        .AsNoTracking()
                        .Include(e => e.Grade)
                        .Include(e => e.Skills).ThenInclude(es => es.Skill)
                        .Where(e => e.IsActive)
                        .OrderBy(e => e.Name)
                        .ToListAsync();

                    return engineers.Select(e => new
                    {
                        id = e.Id,
                        name = e.Name,
                        grade = e.Grade.Name,
                        unitSalePrice = e.Grade.UnitSalePrice,
                        unitCostPrice = e.Grade.UnitCostPrice,
                        skills = e.Skills.Select(es => new
                        {
                            skillId = es.SkillId,
                            skillName = es.Skill.Name,
                            level = es.Level,
                        }).ToList(),
                    }).ToList();
                },
                MakeTool("get_active_engineers",
                    "アクティブなエンジニアの一覧を取得します。エンジニアID、名前、等級、販売単価(unitSalePrice)、原価単価(unitCostPrice)、スキル情報が含まれます。社用開発テーマのコスト計算にはunitCostPriceを使用してください。")),

            // ─── エンジニア稼働余力取得 ───
            AIFunctionFactory.Create(
                async (
                    [Description("エンジニアID")] int engineerId,
                    [Description("年（例: 2025）")] int year,
                    [Description("月（1〜12）")] int month) =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var allocationService = scope.ServiceProvider.GetRequiredService<IAllocationService>();

                    var engineer = await db.Engineers
                        .AsNoTracking()
                        .Include(e => e.Grade)
                        .FirstOrDefaultAsync(e => e.Id == engineerId);

                    if (engineer == null)
                        return (object)new { error = $"エンジニアID {engineerId} が見つかりません" };

                    var maxHours = allocationService.GetMaxDevelopableHours(engineerId, year, month);
                    var allocatedHours = allocationService.GetTotalAllocatedHoursByEngineer(engineerId, year, month);
                    var remainingHours = maxHours - allocatedHours;

                    return (object)new
                    {
                        engineerId,
                        engineerName = engineer.Name,
                        year,
                        month,
                        maxDevelopableHours = maxHours,
                        totalAllocatedHours = allocatedHours,
                        remainingHours,
                        workRate = maxHours > 0 ? Math.Round(allocatedHours / maxHours * 100, 1) : 0m,
                    };
                },
                MakeTool("get_engineer_availability",
                    "特定の年月における1名のエンジニアの稼働余力（最大稼働時間・割り当て済み時間・残余時間）を取得します。")),

            // ─── 全エンジニア稼働余力取得 ───
            AIFunctionFactory.Create(
                async (
                    [Description("年（例: 2025）")] int year,
                    [Description("月（1〜12）")] int month) =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var capacitySettings = scope.ServiceProvider.GetRequiredService<ICapacitySettings>();

                    var engineers = await db.Engineers
                        .AsNoTracking()
                        .Include(e => e.Grade)
                        .Include(e => e.Skills).ThenInclude(es => es.Skill)
                        .Where(e => e.IsActive)
                        .OrderBy(e => e.Name)
                        .ToListAsync();

                    var engineerIds = engineers.Select(e => e.Id).ToList();

                    var monthlyWorkDays = await db.MonthlyWorkDays
                        .AsNoTracking()
                        .FirstOrDefaultAsync(m => m.Year == year && m.Month == month);

                    var adjustments = await db.EngineerMonthlyAdjustments
                        .AsNoTracking()
                        .Where(a => engineerIds.Contains(a.EngineerId) && a.Year == year && a.Month == month)
                        .ToListAsync();

                    var allocations = await db.EngineerThemeAllocations
                        .AsNoTracking()
                        .Where(a => engineerIds.Contains(a.EngineerId) && a.Year == year && a.Month == month)
                        .ToListAsync();

                    return engineers.Select(e =>
                    {
                        var adjustment = adjustments.FirstOrDefault(a => a.EngineerId == e.Id);
                        int workDays = adjustment?.WorkDays ?? monthlyWorkDays?.WorkDays ?? 0;
                        var maxHours = workDays * 8m * capacitySettings.Coefficient;
                        var allocated = allocations.Where(a => a.EngineerId == e.Id).Sum(a => a.AllocatedHours);
                        var remaining = maxHours - allocated;
                        return new
                        {
                            engineerId = e.Id,
                            engineerName = e.Name,
                            grade = e.Grade.Name,
                            unitSalePrice = e.Grade.UnitSalePrice,
                            unitCostPrice = e.Grade.UnitCostPrice,
                            skills = e.Skills.Select(es => new
                            {
                                skillId = es.SkillId,
                                skillName = es.Skill.Name,
                                level = es.Level,
                            }).ToList(),
                            maxDevelopableHours = maxHours,
                            totalAllocatedHours = allocated,
                            remainingHours = remaining,
                            workRate = maxHours > 0 ? Math.Round(allocated / maxHours * 100, 1) : 0m,
                        };
                    }).ToList();
                },
                MakeTool("get_all_engineers_availability",
                    "特定の年月における全アクティブエンジニアの稼働余力一覧を取得します。スキル情報や稼働率も含まれます。社用開発テーマにはunitCostPriceを使用してください。")),

            // ─── テーマの割り当て状況取得 ───
            AIFunctionFactory.Create(
                async (
                    [Description("テーマID")] int themeId,
                    [Description("年（例: 2025）")] int year,
                    [Description("月（1〜12）")] int month) =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var theme = await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == themeId);
                    if (theme == null)
                        return (object)new { error = $"テーマID {themeId} が見つかりません" };

                    bool useCostPrice = theme.OrderType == "社用開発";

                    var allAllocations = (await db.EngineerThemeAllocations
                        .AsNoTracking()
                        .Include(a => a.Engineer).ThenInclude(e => e.Grade)
                        .Where(a => a.ThemeId == themeId)
                        .ToListAsync())
                        .Where(a => a.Engineer?.Grade != null)
                        .ToList();

                    var monthAllocations = allAllocations
                        .Where(a => a.Year == year && a.Month == month)
                        .ToList();

                    var totalCost = allAllocations.Sum(a =>
                        a.AllocatedHours * (useCostPrice ? a.Engineer.Grade.UnitCostPrice : a.Engineer.Grade.UnitSalePrice));

                    return (object)new
                    {
                        themeId,
                        themeName = theme.Name,
                        orderType = theme.OrderType,
                        orderAmount = theme.OrderAmount,
                        totalAllocatedCost = totalCost,
                        remainingBudget = theme.OrderAmount - totalCost,
                        allocations = monthAllocations.Select(a => new
                        {
                            engineerName = a.Engineer.Name,
                            grade = a.Engineer.Grade.Name,
                            hours = a.AllocatedHours,
                            cost = a.AllocatedHours * (useCostPrice ? a.Engineer.Grade.UnitCostPrice : a.Engineer.Grade.UnitSalePrice),
                        }).ToList(),
                    };
                },
                MakeTool("get_theme_allocations",
                    "テーマの特定月における割り当て状況（割り当てエンジニア・時間・コスト・予算残高）を取得します。社用開発テーマは原価（unitCostPrice）でコストを計算します。")),

            // ─── エンジニアをテーマにアサイン ───
            AIFunctionFactory.Create(
                async (
                    [Description("エンジニアID")] int engineerId,
                    [Description("テーマID")] int themeId,
                    [Description("年（例: 2025）")] int year,
                    [Description("月（1〜12）")] int month,
                    [Description("割り当て時間（時間単位、0より大きい値）")] decimal hours) =>
                {
                    if (month < 1 || month > 12)
                        return new { success = false, message = $"月は 1〜12 の範囲で指定してください（指定値: {month}）" };
                    if (year < 2000 || year > 2100)
                        return new { success = false, message = $"年は 2000〜2100 の範囲で指定してください（指定値: {year}）" };

                    using var scope = _scopeFactory.CreateScope();
                    var allocationService = scope.ServiceProvider.GetRequiredService<IAllocationService>();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var engineer = await db.Engineers.AsNoTracking()
                        .Include(e => e.Grade)
                        .FirstOrDefaultAsync(e => e.Id == engineerId);
                    var theme = await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == themeId);

                    if (engineer == null)
                        return new { success = false, message = $"エンジニアID {engineerId} が見つかりません" };
                    if (theme == null)
                        return new { success = false, message = $"テーマID {themeId} が見つかりません" };

                    try
                    {
                        await allocationService.UpsertAllocationAsync(engineerId, themeId, year, month, hours);
                        return new
                        {
                            success = true,
                            message = $"{engineer.Name} を {theme.Name} に {year}/{month:D2} {hours}h 割り当てました。",
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "assign_engineer_to_theme: 割り当て処理中にエラーが発生しました。 engineerId={EngineerId}, themeId={ThemeId}, year={Year}, month={Month}, hours={Hours}", engineerId, themeId, year, month, hours);
                        return new { success = false, message = ex.Message };
                    }
                },
                new AIFunctionFactoryOptions
                {
                    Name = "assign_engineer_to_theme",
                    Description = "エンジニアを特定のテーマ・月に割り当てます（稼働上限・受注金額チェックあり）。",
                }),
        ];
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Azure CLI 認証情報を使用して指定スコープのトークンを取得する TokenCredential。
/// </summary>
internal sealed class AzureApiCredential : TokenCredential
{
    private readonly AzureCliCredential _inner = new();

    public override AccessToken GetToken(TokenRequestContext _, CancellationToken cancellationToken) =>
        _inner.GetToken(new TokenRequestContext([CopilotAgentService.TokenScope]), cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext _, CancellationToken cancellationToken) =>
        _inner.GetTokenAsync(new TokenRequestContext([CopilotAgentService.TokenScope]), cancellationToken);
}

/// <summary>
/// エージェントセッション。選択したプロバイダーとの会話履歴を保持します。
/// </summary>
public class CopilotAgentSession : IAsyncDisposable
{
    private readonly IChatClient? _chatClient;
    private readonly CopilotClient? _copilotClient;
    private readonly CopilotSession? _copilotSession;
    private readonly AIFunction[] _tools;
    private readonly List<ChatMessage> _history;
    private readonly ILogger _logger;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public CopilotAgentSession(IChatClient chatClient, AIFunction[] tools, string systemPrompt, ILogger logger)
    {
        _chatClient = chatClient;
        _tools = tools;
        _history = [new ChatMessage(ChatRole.System, systemPrompt)];
        _logger = logger;
    }

    public CopilotAgentSession(CopilotClient client, CopilotSession session, ILogger logger)
    {
        _copilotClient = client;
        _copilotSession = session;
        _tools = [];
        _history = [];
        _logger = logger;
    }

    /// <summary>
    /// メッセージを送信し、ストリーミングで応答を受け取ります。
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_copilotSession != null)
        {
            var response = await _copilotSession.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cancellationToken);
            var content = response?.Data?.Content;
            if (!string.IsNullOrEmpty(content))
                yield return content;
            yield break;
        }

        var chatClient = _chatClient
            ?? throw new InvalidOperationException("チャットクライアントが初期化されていません");
        _history.Add(new ChatMessage(ChatRole.User, prompt));
        var options = new ChatOptions { Tools = [.._tools] };
        var fullContent = new StringBuilder();

        await foreach (var update in chatClient.GetStreamingResponseAsync(_history, options, cancellationToken))
        {
            if (update.Text is not null)
            {
                fullContent.Append(update.Text);
                yield return update.Text;
            }
        }

        if (fullContent.Length > 0)
            _history.Add(new ChatMessage(ChatRole.Assistant, fullContent.ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        _chatClient?.Dispose();
        if (_copilotSession != null)
            await _copilotSession.DisposeAsync();
        if (_copilotClient != null)
            await _copilotClient.DisposeAsync();
    }
}
