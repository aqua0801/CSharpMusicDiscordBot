using NetCord;
using NetCord.Rest;

namespace DiscordBot;

public static class InteractionExtensions
{
    public static bool ToEphemeral(this DisplayOption display) => display == DisplayOption.Hide;

    public static MessageFlags? ToEphemeralFlag(this DisplayOption display)
        => display.ToEphemeral() ? MessageFlags.Ephemeral : null;

    public static async Task SendFollowupMessageAsync(this Interaction interaction, string content, DisplayOption display)
    {
        await interaction.SendFollowupMessageAsync(new InteractionMessageProperties
        {
            Content = content,
            Flags = display.ToEphemeralFlag(),
        });
    }

    /// <summary>
    /// When a command blows up, try to reply to the user instead of leaving them staring at "thinking..." forever.
    /// We don't know whether it was already deferred, so try a response first and fall back to a followup.
    /// </summary>
    public static async Task TryReportErrorAsync(this Interaction interaction, string content)
    {
        var message = new InteractionMessageProperties
        {
            Content = content,
            Flags = MessageFlags.Ephemeral,
        };

        try
        {
            await interaction.SendResponseAsync(InteractionCallback.Message(message));
            return;
        }
        catch (RestException)
        {
            // Already responded (usually deferred), use a followup instead
        }

        try
        {
            await interaction.SendFollowupMessageAsync(message);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to report error message to user", ex);
        }
    }
}

public static class AnsiHelper
{
    private const string Esc = "\u001b";

    public static string ToColored(this string text, AnsiColor color, bool bold = false)
        => $"{Esc}[{(bold ? 1 : 0)};{(int)color}m{text}{Esc}[0m";

    public static string WrapAnsiBlock(string content) => $"```ansi\n{content}\n```";

    public enum AnsiColor
    {
        Gray = 30,
        Red = 31,
        Green = 32,
        Yellow = 33,
        Blue = 34,
        Pink = 35,
        Cyan = 36,
        White = 37,
    }
}
