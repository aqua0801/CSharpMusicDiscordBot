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


