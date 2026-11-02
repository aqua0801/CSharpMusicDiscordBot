using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordBot;

/// <summary>
/// Shared System.Text.Json options and file I/O.
/// The old Newtonsoft JObject.GetValueOrDefault approach swallowed errors into default without logging
/// (JsonParseErrorLog built the string and then discarded it). Now everything deserializes into strong types,
/// and malformed input throws a JsonException that includes the JSON path.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = true,
            // Keep Chinese playlist names readable when saved instead of \uXXXX escapes
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new FlexibleStringConverter());
        return options;
    }

    /// <summary>Reads and deserializes; throws FileNotFoundException if missing, JsonException (with file name) if malformed.</summary>
    public static T Read<T>(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"JSON file not found: {fullPath}", fullPath);

        try
        {
            using var stream = File.OpenRead(fullPath);
            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new JsonException("JSON content is null");
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Failed to parse {fullPath} ({typeof(T).Name}): {ex.Message}", ex.Path, ex.LineNumber, ex.BytePositionInLine, ex);
        }
    }

    /// <summary>
    /// Writes to a temp file and renames over the original, so a crash mid-write can't corrupt it.
    /// (the old JWriter had a check-then-act race, and a half-finished synchronous File.WriteAllText left a broken file)
    /// </summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        string fullPath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tempPath = fullPath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, value, Options);
        }
        File.Move(tempPath, fullPath, overwrite: true);
    }

    public static T Deserialize<T>(string json, string sourceDescription)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException("JSON content is null");
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Failed to parse {sourceDescription} response ({typeof(T).Name}): {ex.Message}{Environment.NewLine}Raw content: {Truncate(json, 500)}", ex.Path, ex.LineNumber, ex.BytePositionInLine, ex);
        }
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Lets string fields accept JSON strings / numbers / booleans.
/// Many ids in config files and the Hoyolab API are sometimes "123" and sometimes 123 (Newtonsoft silently converts, STJ doesn't).
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override bool HandleNull => false;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(
                reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to string"),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
