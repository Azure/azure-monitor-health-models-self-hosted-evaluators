using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfHostedEvaluators.Core.Configuration;

internal sealed class StrictJson(JsonElement element, string variable, string path = "$")
{
    public JsonElement Element { get; } = element;
    public string Variable { get; } = variable;
    public string Path { get; } = path;
    public ConfigurationException Error(string? property = null) =>
        ConfigurationException.At(Variable, property is null ? Path : Path + "." + property);

    public static JsonDocument Parse(string json, string variable)
    {
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw ConfigurationException.At(variable, "$");
                CheckDuplicates(document.RootElement, variable, "$");
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw ConfigurationException.At(variable, "$");
        }
    }

    private static void CheckDuplicates(JsonElement element, string variable, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw ConfigurationException.At(variable, path + "." + property.Name);
                CheckDuplicates(property.Value, variable, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
                CheckDuplicates(item, variable, $"{path}[{i++}]");
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            // JsonDocument defers unescaping; force validation even in opaque extension JSON.
            _ = element.GetString();
        }
    }

    public StrictJson Shape(params string[] allowed)
    {
        if (Element.ValueKind != JsonValueKind.Object) throw Error();
        foreach (var property in Element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Error(property.Name);
        return this;
    }

    public bool Has(string property) => Element.TryGetProperty(property, out _);
    public StrictJson Child(string property)
    {
        if (!Element.TryGetProperty(property, out var child)) throw Error(property);
        return new(child, Variable, Path + "." + property);
    }
    public string Text()
    {
        if (Element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(Element.GetString())) throw Error();
        return Element.GetString()!;
    }
    public string Text(string property) => Child(property).Text();
    public string? OptionalText(string property) => Has(property) ? Text(property) : null;
    public int Integer(int min = 1, int max = int.MaxValue)
    {
        if (Element.ValueKind != JsonValueKind.Number || !Element.TryGetInt32(out var value) || value < min || value > max)
            throw Error();
        return value;
    }
    public double Number()
    {
        if (Element.ValueKind != JsonValueKind.Number || !Element.TryGetDouble(out var value) || !double.IsFinite(value))
            throw Error();
        return value;
    }
    public double Multiplier()
    {
        var number = Number();
        if (number <= 0) throw Error();
        return number;
    }
    public TimeSpan Duration() => ParseDuration(Text(), Variable, Path);
    public static TimeSpan ParseDuration(string text, string variable, string path)
    {
        if (!Regex.IsMatch(text, @"^[0-9]+[smhd]\z", RegexOptions.CultureInvariant) ||
            !long.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
            throw ConfigurationException.At(variable, path);
        var unit = text[^1] switch
        {
            's' => TimeSpan.TicksPerSecond, 'm' => TimeSpan.TicksPerMinute,
            'h' => TimeSpan.TicksPerHour, _ => TimeSpan.TicksPerDay
        };
        if (count > long.MaxValue / unit) throw ConfigurationException.At(variable, path);
        return TimeSpan.FromTicks(count * unit);
    }
    public static TimeSpan Interval(TimeSpan value, string variable, string path)
    {
        if (value < TimeSpan.FromSeconds(10) || value > TimeSpan.FromDays(1))
            throw ConfigurationException.At(variable, path);
        return value;
    }
    public Uri Https()
    {
        try
        {
            if (!Uri.TryCreate(Text(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw Error();
            // Uri defers normalization, including IDN validation, until these properties are read.
            _ = uri.IdnHost;
            _ = uri.AbsoluteUri;
            return uri;
        }
        catch (UriFormatException)
        {
            throw Error();
        }
    }
    public Uri Secret()
    {
        var text = Text();
        if (!Regex.IsMatch(text, @"^kv://[a-zA-Z0-9][a-zA-Z0-9-]*/[a-zA-Z0-9-]+(?:/[a-zA-Z0-9-]+)?\z", RegexOptions.CultureInvariant)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) throw Error();
        return uri;
    }
    public string Name()
    {
        var name = Text();
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]\z", RegexOptions.CultureInvariant)) throw Error();
        return name;
    }
    public T EnumValue<T>() where T : struct, Enum
    {
        var text = Text();
        if (!Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)) throw Error();
        return Enum.Parse<T>(text);
    }
    public IEnumerable<StrictJson> Items()
    {
        if (Element.ValueKind != JsonValueKind.Array || Element.GetArrayLength() == 0) throw Error();
        var index = 0;
        foreach (var item in Element.EnumerateArray()) yield return new(item, Variable, $"{Path}[{index++}]");
    }
}
