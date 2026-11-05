using System.Text.Encodings.Web;
using System.Text.Json;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     The canary set of AB#6914 / AB#7015 (fake values, never real secrets) and every textual form a serializer, a URL or a
///     JSON-in-JSON log line can turn them into. A hit of any form in any sink is a leak.
/// </summary>
internal static class Canaries
{
    /// <summary>Plain canary without characters a serializer escapes.</summary>
    public const string Plain = "CANARY-SECRET-123";

    /// <summary>
    ///     A canary that every JSON encoder escapes (AB#7015): quote, backslash, plus, ampersand, angle brackets, apostrophe,
    ///     slash, non-ASCII letters, a surrogate pair and control characters.
    /// </summary>
    public const string Escaping = "CANARY-\"q\\b+&<>'/-Äö-\U0001F600-\t\u0007-END";

    /// <summary>A fake bearer token; sent as the Authorization header of a forged request.</summary>
    public const string ForgedToken = "CANARY-TOKEN-456";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    ///     The plain value, its JSON-escaped forms (System.Text.Json default and relaxed encoder, Newtonsoft.Json, upper- and
    ///     lower-case hex), the same once more for JSON embedded in a JSON string, and the percent-encoded form.
    /// </summary>
    public static IReadOnlyList<string> Forms(string secret)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal) { secret };
        foreach (var once in new[]
                 {
                     Inner(JsonSerializer.Serialize(secret)),
                     Inner(JsonSerializer.Serialize(secret, Relaxed)),
                     Inner(Newtonsoft.Json.JsonConvert.SerializeObject(secret)),
                     Inner(Newtonsoft.Json.JsonConvert.SerializeObject(secret,
                         new Newtonsoft.Json.JsonSerializerSettings
                             { StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.EscapeNonAscii }))
                 })
        {
            forms.Add(once);
            forms.Add(LowerHex(once));
            forms.Add(Inner(JsonSerializer.Serialize(once)));
            forms.Add(Inner(JsonSerializer.Serialize(once, Relaxed)));
            forms.Add(Inner(Newtonsoft.Json.JsonConvert.SerializeObject(once)));
        }

        forms.Add(Uri.EscapeDataString(secret));
        forms.Add(System.Net.WebUtility.UrlEncode(secret));
        // A form shorter than the value's distinctive prefix would match unrelated text; every form keeps "CANARY".
        return forms.Where(f => f.Length >= 8).ToList();
    }

    /// <summary>A JWT and the parts that identify it: the whole token and its signature.</summary>
    public static IReadOnlyList<string> TokenForms(string jwt)
    {
        var parts = jwt.Split('.');
        var forms = new List<string> { jwt };
        if (parts.Length == 3 && parts[2].Length >= 16)
        {
            forms.Add(parts[2]);
        }

        return forms;
    }

    private static string Inner(string json) => json.Length >= 2 && json[0] == '"' ? json[1..^1] : json;

    private static string LowerHex(string escaped)
    {
        var chars = escaped.ToCharArray();
        for (var i = 0; i + 5 < chars.Length; i++)
        {
            if (chars[i] == '\\' && chars[i + 1] == 'u')
            {
                for (var k = i + 2; k < i + 6; k++)
                {
                    chars[k] = char.ToLowerInvariant(chars[k]);
                }
            }
        }

        return new string(chars);
    }
}
