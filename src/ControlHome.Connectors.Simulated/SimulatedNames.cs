namespace ControlHome.Connectors.Simulated;

/// <summary>
/// The simulated home's names in Portuguese: devices, their controls and the rooms they suggest are named in English in
/// code and translated here, so a pt-BR home (config <c>language</c>) gets "Luz da sala" in "Sala" — matching its rooms.
/// </summary>
internal static class SimulatedNames
{
    public const string English = "en";
    public const string Portuguese = "pt-BR";

    public static IReadOnlyList<string> Languages { get; } = [English, Portuguese];

    private static readonly Dictionary<string, string> _portuguese = new(StringComparer.Ordinal)
    {
        // Devices.
        ["Living room light"] = "Luz da sala",
        ["Coffee maker plug"] = "Tomada da cafeteira",
        ["Bedroom air conditioner"] = "Ar-condicionado do quarto",
        ["Front door sensor"] = "Sensor da porta",
        ["Front door lock"] = "Fechadura da porta",
        ["Bedroom lamp"] = "Abajur do quarto",
        ["Washing machine"] = "Máquina de lavar",
        ["Living room TV"] = "TV da sala",
        ["Office motion sensor"] = "Sensor de presença do escritório",
        ["Living room wall switch"] = "Interruptor da sala",
        ["Recorder"] = "Gravador",
        ["Gate camera"] = "Câmera do portão",
        ["Garage camera"] = "Câmera da garagem",
        ["Backyard camera"] = "Câmera do quintal",
        ["Front porch camera"] = "Câmera da varanda",
        ["Gate"] = "Portão",
        ["Living room blinds"] = "Persiana da sala",
        ["Water tank"] = "Caixa d'água",
        ["Bathroom sensor"] = "Sensor do banheiro",
        ["Fridge plug"] = "Tomada da geladeira",

        // Controls of the 3-gang switch.
        ["Ceiling"] = "Teto",
        ["Wall lights"] = "Arandelas",
        ["Porch"] = "Varanda",

        // Rooms.
        ["Living room"] = "Sala",
        ["Kitchen"] = "Cozinha",
        ["Bedroom"] = "Quarto",
        ["Entrance"] = "Entrada",
        ["Laundry"] = "Lavanderia",
        ["Office"] = "Escritório",
        ["Garage"] = "Garagem",
        ["Backyard"] = "Quintal",
        ["Bathroom"] = "Banheiro",
    };

    /// <summary>An English name in <paramref name="language"/> (unchanged in English, or when it has no translation).</summary>
    public static string In(string language, string english) =>
        language == Portuguese && _portuguese.TryGetValue(english, out var translated) ? translated : english;
}
