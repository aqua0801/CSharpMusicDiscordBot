using NetCord;
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

}


public readonly struct DisplayedMessage
{
    public readonly string Content;
    public readonly MessageFlags? Flags;

    public DisplayedMessage(string content, DisplayOption display)
    {
        Content = content;
        Flags = display.ToEphemeralFlag();
    }

    public static implicit operator InteractionMessageProperties(DisplayedMessage msg)
        => new InteractionMessageProperties
        {
            Content = msg.Content,
            Flags = msg.Flags
        };

    public static implicit operator DisplayedMessage((string content, DisplayOption display) tuple)
            => new DisplayedMessage(tuple.content, tuple.display);
}
