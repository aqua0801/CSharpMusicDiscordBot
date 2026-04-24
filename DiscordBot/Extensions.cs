using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

public static class ExtensionMethods
{
    public static bool ToEphemeral(this DisplayOption display)
    {
        return SlashCommands.ToEphemeral(display);
    }

    public static MessageFlags? ToEphemeralFlag(this DisplayOption display)
    {
        return (display.ToEphemeral()) ? MessageFlags.Ephemeral : null;
    }

    public static async Task SendFollowupMessageAsync(this Interaction interaction , string content , DisplayOption display)
    {
        await interaction.SendFollowupMessageAsync(new InteractionMessageProperties()
        {
            Content = content,
            Flags = display.ToEphemeralFlag()
        });
    }

    public static async Task LeaveVoiceChannel(this GatewayClient client , ulong guildId)
    {
        await client.UpdateVoiceStateAsync(new VoiceStateProperties(guildId , null));
    }

}

public static class AnsiHelper
{
    private const string Esc = "\u001b";

    public static string ToColored(this string text, AnsiColor color, bool bold = false)
    {
        string format = bold ? "1" : "0";
        return $"{Esc}[{format};{(int)color}m{text}{Esc}[0m";
    }
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
        White = 37
    }
}




