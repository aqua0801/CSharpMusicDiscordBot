using System.Text.Json.Serialization;

namespace DiscordBot;

/// <summary>Maps to Data/env.json.</summary>
public sealed record BotConfig
{
    [JsonPropertyName("bot_token")]
    public string BotToken { get; init; } = "";

    [JsonPropertyName("bot_nickname")]
    public string BotNickname { get; init; } = "機器人";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.0.0.0";

    [JsonPropertyName("git_repo_link")]
    public string GitUrl { get; init; } = "";

    [JsonPropertyName("git_repo_link2")]
    public string GitUrl2 { get; init; } = "";

    /// <summary>Upload limit (MB) per server boost tier (0~3).</summary>
    [JsonPropertyName("server_tier_file_size")]
    public List<int> ServerTierUploadFileSize { get; init; } = [10, 10, 50, 100];

    [JsonPropertyName("prefix")]
    public char CommandPrefix { get; init; } = '!';

    [JsonPropertyName("resin_loop_check")]
    public bool ResinLoopCheck { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BotToken))
            throw new InvalidDataException($"{GlobalVariable.EnvJsonFilePath} is missing bot_token");
        if (ServerTierUploadFileSize is null || ServerTierUploadFileSize.Count == 0)
            throw new InvalidDataException($"{GlobalVariable.EnvJsonFilePath}: server_tier_file_size must not be empty");
    }

    /// <summary>Gets the upload limit (MB) for the boost tier; falls back to the last entry if out of range.</summary>
    public int GetUploadLimitMB(int premiumTier)
        => ServerTierUploadFileSize[Math.Clamp(premiumTier, 0, ServerTierUploadFileSize.Count - 1)];
}
