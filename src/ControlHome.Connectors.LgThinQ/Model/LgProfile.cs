using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.LgThinQ.Api;

namespace ControlHome.Connectors.LgThinQ.Model;

/// <summary>A ThinQ profile property type (<c>type</c>).</summary>
internal enum LgPropertyType
{
    Enum,
    Range,
    Boolean,
    Number,

    /// <summary>A string: a constant written as the property's profile value (e.g. <c>"detergentSetting": "NORMAL"</c>) or free text.</summary>
    String,

    /// <summary>Lists and unknown types: read-only JSON.</summary>
    Other,
}

/// <summary>A range's bounds (<c>{min, max, step, except}</c>).</summary>
internal sealed record LgRange(double Min, double Max, double Step, IReadOnlyList<double> Except)
{
    public bool Allows(double value) =>
        value >= Min - 1e-9 && value <= Max + 1e-9 && !Except.Any(e => Math.Abs(e - value) < 1e-9)
        && (Step <= 0 || Math.Abs(Math.IEEERemainder(value - Min, Step)) < 1e-6);

    public bool IsInteger => IsWhole(Min) && IsWhole(Max) && IsWhole(Step);

    private static bool IsWhole(double value) => Math.Abs(value - Math.Round(value)) < 1e-9;
}

/// <summary>
/// Where a property lives in a device's status (and so in a control payload): an optional sub-device key (a WashTower's
/// <c>washer</c> / <c>dryer</c>), an optional location (a washer's <c>{"location": {"locationName": "MAIN"}}</c>), the
/// resource, and for resources that are lists, the list entry (<c>locationName</c> of a refrigerator compartment, or the
/// <c>unit</c> of an air conditioner's <c>temperatureInUnits</c>).
/// </summary>
internal sealed record LgPlace(string? Sub, string? Location, string Resource, string? Selector, string? SelectorValue)
{
    public const string LocationNameSelector = "locationName";
    public const string UnitSelector = "unit";

    /// <summary>The endpoint this place belongs to: the sub-device, the location, a list entry's location, else <c>main</c>.</summary>
    public string EndpointKey =>
        Sub is not null ? EndpointKeys.Of(Sub)
        : Selector == LocationNameSelector && SelectorValue is not null ? EndpointKeys.Of(SelectorValue)
        : Location is not null ? EndpointKeys.Of(Location)
        : EndpointKeys.Main;

    /// <summary>The resource object of this place in <paramref name="status"/>, or <c>null</c>.</summary>
    public JsonElement? ResourceIn(JsonElement status)
    {
        if (Scope(status) is not { } scope || scope.Get(Resource) is not { } resource)
        {
            return null;
        }

        if (Selector is null)
        {
            return resource.ValueKind == JsonValueKind.Object ? resource : null;
        }

        return resource.Items().FirstOrDefault(item => string.Equals(item.Str(Selector), SelectorValue, StringComparison.Ordinal)) is
        { ValueKind: JsonValueKind.Object } entry
            ? entry
            : null;
    }

    /// <summary>What a control call for this place addresses: calls with the same target can be merged.</summary>
    public string Target => $"{Sub}|{Location}|{(Selector == LocationNameSelector ? Resource + ":" + SelectorValue : string.Empty)}";

    /// <summary>The control call that sets <paramref name="key"/> = <paramref name="value"/> here.</summary>
    public LgCall Call(string key, JsonNode? value) => new(Payload(key, value), Target);

    /// <summary>The control payload that sets <paramref name="key"/> = <paramref name="value"/> here.</summary>
    public JsonObject Payload(string key, JsonNode? value)
    {
        var inner = new JsonObject();
        if (Selector == LocationNameSelector)
        {
            inner[LocationNameSelector] = SelectorValue;
        }

        inner[key] = value;
        if (Selector == UnitSelector)
        {
            inner[UnitSelector] = SelectorValue;
        }

        var payload = new JsonObject();
        if (Location is not null)
        {
            payload["location"] = new JsonObject { [LocationNameSelector] = Location };
        }

        payload[Resource] = inner;
        return Sub is null ? payload : new JsonObject { [Sub] = payload };
    }

    private JsonElement? Scope(JsonElement status)
    {
        var scope = status;
        if (Sub is not null)
        {
            if (scope.Get(Sub) is not { } sub)
            {
                return null;
            }

            scope = sub;
        }

        if (Location is null)
        {
            return scope.ValueKind == JsonValueKind.Object ? scope : null;
        }

        if (scope.ValueKind == JsonValueKind.Array)
        {
            return scope.Items().FirstOrDefault(item => LocationOf(item) == Location) is { ValueKind: JsonValueKind.Object } found ? found : null;
        }

        return scope.ValueKind == JsonValueKind.Object && (LocationOf(scope) is null || LocationOf(scope) == Location) ? scope : null;
    }

    public static string? LocationOf(JsonElement entry) => entry.Get("location")?.Str(LocationNameSelector);
}

/// <summary>One property of a device profile, with its place, type, access and allowed values.</summary>
internal sealed record LgProperty(
    LgPlace Place,
    string Key,
    LgPropertyType Type,
    bool Readable,
    bool Writable,
    IReadOnlyList<string> ReadValues,
    IReadOnlyList<string> WriteValues,
    LgRange? ReadRange,
    LgRange? WriteRange,
    string? Unit,
    string? Constant)
{
    public string Resource => Place.Resource;

    /// <summary>Every value the property may take (enums): read values then the write-only ones.</summary>
    public IReadOnlyList<string> Values => [.. ReadValues.Concat(WriteValues).Distinct(StringComparer.Ordinal)];

    /// <summary>The range of values, read and write combined.</summary>
    public LgRange? Range => (ReadRange, WriteRange) switch
    {
        (null, null) => null,
        ({ } r, null) => r,
        (null, { } w) => w,
        ({ } r, { } w) => new LgRange(Math.Min(r.Min, w.Min), Math.Max(r.Max, w.Max), w.Step, w.Except),
    };

    /// <summary>The property's current value in <paramref name="status"/>, or <c>null</c> when absent.</summary>
    public JsonElement? Read(JsonElement status) => Place.ResourceIn(status)?.Get(Key);

    public LgCall Call(JsonNode? value) => Place.Call(Key, value);

    /// <summary>Whether <paramref name="value"/> may be written (enum: a write value; range: within the write range).</summary>
    public bool AllowsWrite(JsonElement value) => Writable && Type switch
    {
        LgPropertyType.Enum => value.ValueKind == JsonValueKind.String && (WriteValues.Count == 0 || WriteValues.Contains(value.GetString()!, StringComparer.Ordinal)),
        LgPropertyType.Range => value.ValueKind == JsonValueKind.Number && (WriteRange ?? ReadRange) is var range && (range is null || range.Allows(value.GetDouble())),
        LgPropertyType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        LgPropertyType.Number => value.ValueKind == JsonValueKind.Number,
        LgPropertyType.String => value.ValueKind == JsonValueKind.String,
        _ => false,
    };

    public override string ToString() => $"{Place.Sub}/{Place.Location}/{Resource}[{Place.SelectorValue}].{Key}";
}

/// <summary>One parsed device profile: every property (in profile order), the push notification codes and the sub-devices.</summary>
internal sealed record LgProfile(IReadOnlyList<LgProperty> Properties, IReadOnlyList<(string? Sub, string Code)> Notifications)
{
    public static LgProfile Empty { get; } = new([], []);

    /// <summary>
    /// Parses a profile (<c>GET devices/{id}/profile</c>): <c>property</c> as an object of resources or, for washers, a list
    /// of locations; a WashTower's <c>washer</c> / <c>dryer</c> sub-profiles; resources that are lists of compartments
    /// (<c>locationName</c>) or of temperature units (<c>unit</c>). Anything unexpected is skipped, never thrown.
    /// </summary>
    public static LgProfile Parse(JsonElement profile)
    {
        var properties = new List<LgProperty>();
        var notifications = new List<(string?, string)>();
        if (profile.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        if (profile.Get("property") is { } property)
        {
            AddNotifications(profile, null, notifications);
            AddPropertyRoot(property, null, properties);
        }
        else
        {
            // WashTower-style: one sub-profile per sub-device.
            foreach (var sub in profile.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.Get("property") is not null))
            {
                AddNotifications(sub.Value, sub.Name, notifications);
                AddPropertyRoot(sub.Value.Get("property")!.Value, sub.Name, properties);
            }
        }

        return new LgProfile(properties, notifications);
    }

    /// <summary>The sub-device keys (a WashTower's <c>washer</c>, <c>dryer</c>), in profile order.</summary>
    public IReadOnlyList<string> Subs => [.. Properties.Select(p => p.Place.Sub).OfType<string>().Distinct(StringComparer.Ordinal)];

    private static void AddNotifications(JsonElement profile, string? sub, List<(string?, string)> notifications)
    {
        foreach (var code in profile.Get("notification")?.Array("push") ?? [])
        {
            if (code.ValueKind == JsonValueKind.String && code.GetString() is { Length: > 0 } text && !notifications.Contains((sub, text)))
            {
                notifications.Add((sub, text));
            }
        }
    }

    private static void AddPropertyRoot(JsonElement property, string? sub, List<LgProperty> properties)
    {
        if (property.ValueKind == JsonValueKind.Object)
        {
            AddResources(property, sub, null, properties);
            return;
        }

        // Washers: a list of locations, each a set of resources with "location": {"locationName": ...}.
        foreach (var location in property.Items().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            AddResources(location, sub, LgPlace.LocationOf(location) ?? "MAIN", properties);
        }
    }

    private static void AddResources(JsonElement resources, string? sub, string? location, List<LgProperty> properties)
    {
        foreach (var resource in resources.EnumerateObject())
        {
            if (resource.Name == "location")
            {
                continue;
            }

            switch (resource.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    AddProperties(new LgPlace(sub, location, resource.Name, null, null), resource.Value, properties);
                    break;
                case JsonValueKind.Array:
                    foreach (var entry in resource.Value.Items().Where(i => i.ValueKind == JsonValueKind.Object))
                    {
                        if (entry.Str(LgPlace.LocationNameSelector) is { } locationName)
                        {
                            AddProperties(new LgPlace(sub, location, resource.Name, LgPlace.LocationNameSelector, locationName), entry, properties);
                        }
                        else if (entry.Str(LgPlace.UnitSelector) is { } unit)
                        {
                            AddProperties(new LgPlace(sub, location, resource.Name, LgPlace.UnitSelector, unit), entry, properties);
                        }
                    }

                    break;
            }
        }
    }

    private static void AddProperties(LgPlace place, JsonElement resource, List<LgProperty> properties)
    {
        // A resource-level "unit": "C" applies to its properties (old "temperature" resources).
        var resourceUnit = resource.Str("unit");
        foreach (var member in resource.EnumerateObject())
        {
            if (member.Name == place.Selector || member.Name.Length is 0 or > 100 || place.Resource.Length is 0 or > 100)
            {
                continue;
            }

            if (Parse(place, member.Name, member.Value, resourceUnit ?? (place.Selector == LgPlace.UnitSelector ? place.SelectorValue : null)) is { } parsed)
            {
                properties.Add(parsed);
            }
        }
    }

    private static LgProperty? Parse(LgPlace place, string key, JsonElement spec, string? resourceUnit)
    {
        if (spec.ValueKind == JsonValueKind.String)
        {
            // A read-only constant (e.g. "detergentSetting": "NORMAL", or a resource's "unit": "C").
            return key == "unit" ? null : new LgProperty(place, key, LgPropertyType.String, true, false, [spec.GetString()!], [], null, null, null, spec.GetString());
        }

        if (spec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var mode = spec.Array("mode").Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()).ToList();
        var readable = mode.Contains("r");
        var writable = mode.Contains("w");
        if (!readable && !writable)
        {
            return null;
        }

        var type = spec.Str("type") switch
        {
            "enum" => LgPropertyType.Enum,
            "range" => LgPropertyType.Range,
            "boolean" => LgPropertyType.Boolean,
            "number" => LgPropertyType.Number,
            "string" => LgPropertyType.String,
            _ => LgPropertyType.Other,
        };
        var value = spec.Get("value");
        var readValues = readable ? EnumValues(value?.Get("r")) : [];
        var writeValues = writable ? EnumValues(value?.Get("w")) : [];
        if (type == LgPropertyType.Enum && readValues.Count + writeValues.Count > 0
            && readValues.Concat(writeValues).All(v => v is "true" or "false"))
        {
            type = LgPropertyType.Boolean;
        }

        var unit = spec.Str("unit") ?? resourceUnit ?? TemperatureUnitOf(place, key);
        return new LgProperty(
            place,
            key,
            type,
            readable,
            writable,
            readValues,
            writeValues,
            readable ? RangeOf(value?.Get("r")) : null,
            writable ? RangeOf(value?.Get("w")) : null,
            unit,
            null);
    }

    /// <summary><c>targetTemperatureC</c> in a compartment's <c>temperatureInUnits</c> is in °C (and <c>…F</c> in °F).</summary>
    private static string? TemperatureUnitOf(LgPlace place, string key) =>
        place.Resource.StartsWith("temperature", StringComparison.OrdinalIgnoreCase) && key.Contains("Temperature", StringComparison.Ordinal)
            ? key.EndsWith('C') ? "C" : key.EndsWith('F') ? "F" : null
            : null;

    private static IReadOnlyList<string> EnumValues(JsonElement? values) =>
        values is { ValueKind: JsonValueKind.Array } list
            ? [.. list.EnumerateArray().Select(v => v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }).OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)]
            : [];

    private static LgRange? RangeOf(JsonElement? range) =>
        range is { ValueKind: JsonValueKind.Object } r && r.Num("min") is { } min && r.Num("max") is { } max && max >= min
            ? new LgRange(min, max, r.Num("step") is { } step && step > 0 ? step : 1, [.. r.Array("except").Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetDouble())])
            : null;
}

/// <summary>Endpoint keys from LG location and sub-device names.</summary>
internal static class EndpointKeys
{
    public const string Main = "main";

    /// <summary>Lower-case, letters and digits only (<c>FRIDGE</c> → <c>fridge</c>, <c>MAIN</c> → <c>main</c>).</summary>
    public static string Of(string name)
    {
        var key = new string([.. name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        return key.Length == 0 ? Main : key.Length > 64 ? key[..64] : key;
    }

    /// <summary>A display name (<c>FRIDGE</c> → <c>Fridge</c>, <c>washer</c> → <c>Washer</c>).</summary>
    public static string NameOf(string name)
    {
        var words = name.Replace('_', ' ').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
