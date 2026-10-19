using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Windows;

public static class BrowserEnvironmentVerification
{
    public static string ReadExpression(string timezone) =>
        "JSON.stringify({timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,expectedTimezone:new Intl.DateTimeFormat('en-US',{timeZone:" + JsonSerializer.Serialize(timezone) + "}).resolvedOptions().timeZone,language:navigator.language,locale:Intl.DateTimeFormat().resolvedOptions().locale})";

    public static bool TimezoneMatches(JsonObject? observation)
    {
        var actual = Text(observation, "timezone");
        var expected = Text(observation, "expectedTimezone");
        return actual.Length > 0 && expected.Length > 0 && actual.Equals(expected, StringComparison.Ordinal);
    }

    public static bool EnglishEnvironmentMatches(JsonObject? observation) =>
        TimezoneMatches(observation) && Text(observation, "language") == "en-US";

    public static bool EnglishLocaleMatches(JsonObject? observation) => Text(observation, "locale") == "en-US";

    private static string Text(JsonObject? value, string name) =>
        value?[name] is JsonValue text && text.TryGetValue<string>(out var result) ? result : "";
}
