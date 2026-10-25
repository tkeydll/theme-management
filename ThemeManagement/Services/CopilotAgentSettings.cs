using System.Text.Json;

namespace ThemeManagement.Services;

public interface ICopilotAgentSettings
{
    string Provider { get; }
    string Model { get; }
    string[] AvailableModels { get; }
    string? CustomSystemPrompt { get; }
    void Update(string provider, string model, string? systemPrompt);
}

public class CopilotAgentSettings : ICopilotAgentSettings
{
    private const string FileName = "copilot-agent-settings.json";
    private readonly string _filePath;
    private string _provider = "AzureOpenAI";
    private string _model = "gpt-5-mini";
    private string? _systemPrompt;

    public string[] AvailableModels { get; }

    public CopilotAgentSettings(IWebHostEnvironment env, IConfiguration configuration)
    {
        _filePath = Path.Combine(env.ContentRootPath, FileName);
        _provider = configuration["CopilotAgent:Provider"] ?? _provider;
        _model = configuration["CopilotAgent:Model"] ?? _model;
        AvailableModels = configuration.GetSection("CopilotAgent:AvailableModels").Get<string[]>()
            ?? ["gpt-5-mini"];
        Load();
    }

    public string Provider => _provider;
    public string Model => _model;
    public string? CustomSystemPrompt => _systemPrompt;

    public void Update(string provider, string model, string? systemPrompt)
    {
        if (provider is not ("AzureOpenAI" or "CopilotSdk"))
            throw new ArgumentException("未対応のエージェントプロバイダーです", nameof(provider));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("モデル名を入力してください", nameof(model));

        var prompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        var settings = new SettingsData { Provider = provider, Model = model.Trim(), SystemPrompt = prompt };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
        _provider = settings.Provider;
        _model = settings.Model;
        _systemPrompt = settings.SystemPrompt;
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;

        var json = File.ReadAllText(_filePath);
        var settings = JsonSerializer.Deserialize<SettingsData>(json)
            ?? throw new InvalidDataException($"設定ファイルを読み込めません: {_filePath}");
        if (!string.IsNullOrWhiteSpace(settings.Provider))
            _provider = settings.Provider;
        if (!string.IsNullOrWhiteSpace(settings.Model))
            _model = settings.Model;
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            _systemPrompt = settings.SystemPrompt;
    }

    private record SettingsData
    {
        public string Provider { get; init; } = "AzureOpenAI";
        public string Model { get; init; } = "gpt-5-mini";
        public string? SystemPrompt { get; init; }
    }
}
