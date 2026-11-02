using System.Text.Json;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.Commands;

namespace DiscordBot;

/// <summary>
/// An exception that carries a Chinese message meant for Discord users.
/// <see cref="Exception.Message"/> stays in English for logs; <see cref="UserMessage"/> is what users see.
/// Throw this when the user can act on the reason (e.g. wrong uid, private profile).
/// </summary>
public sealed class UserFacingException(string message, string userMessage, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string UserMessage { get; } = userMessage;
}

/// <summary>
/// Converts exceptions and NetCord failure results into Chinese text for users.
/// Rule of thumb: code, comments, logs and exception messages are English;
/// anything sent to Discord goes through here (or is a Chinese literal) so users never see English.
/// </summary>
public static class UserMessages
{
    public static string FromException(Exception ex) => ex switch
    {
        UserFacingException u => u.UserMessage,
        HoyolabApiException api => FromHoyolabRetcode(api.RetCode),
        TimeoutException or TaskCanceledException => "連線逾時，請稍後再試",
        RestException rest => $"Discord API 發生錯誤（HTTP {(int)rest.StatusCode}）",
        HttpRequestException { StatusCode: { } code } => $"網路請求失敗（HTTP {(int)code}）",
        HttpRequestException => "網路請求失敗，請稍後再試",
        JsonException => "收到的資料格式不正確",
        _ => "發生未預期的錯誤，詳細內容已記錄在 log",
    };

    // Common HoYoLAB retcodes; anything else just shows the code.
    private static string FromHoyolabRetcode(int retcode) => retcode switch
    {
        -100 or 10001 or 10103 => "HoYoLAB 登入已過期，請更新 cookie",
        10102 => "HoYoLAB 戰績未公開，請到 HoYoLAB 開啟公開設定",
        1034 or 5003 => "HoYoLAB 要求驗證碼（被風控），請稍後再試",
        _ => $"HoYoLAB 回應錯誤（retcode {retcode}）",
    };

    public static string FromFailResult(IFailResult fail) => fail switch
    {
        MissingPermissionsResult => "權限不足，無法執行這個指令！",
        RequiredNsfwResult => "這個指令只能在 NSFW 頻道使用！",
        InvalidContextResult => "這個指令不能在這裡使用！",
        ParameterCountMismatchResult or ParametersMismatchResult => "指令參數數量不正確！",
        CommandTypeParserFailResult or SlashCommandTypeReaderFailResult => "指令參數格式不正確！",
        _ => "指令執行失敗！",
    };
}
