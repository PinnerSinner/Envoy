namespace Envoy.Core.Ux;

/// <summary>The Livery page's Motion setting: plain labels for each choice (spec 09b, REQ-MOT-1).</summary>
public static class MotionText
{
    public const string Auto = "Follow Windows";
    public const string Full = "Full";
    public const string Reduced = "Fades only";
    public const string Off = "Off";

    public const string Help =
        "Follow Windows animates unless \"Show animations\" is off in Windows settings. Fades only keeps fades and stops everything that moves.";

    /// <summary>The label of a setting value (an unknown value reads as Auto).</summary>
    public static string LabelOf(MotionSetting setting) => setting switch
    {
        MotionSetting.Full => Full,
        MotionSetting.Reduced => Reduced,
        MotionSetting.Off => Off,
        _ => Auto,
    };

    /// <summary>The choices in the order the page lists them.</summary>
    public static IReadOnlyList<MotionSetting> Choices { get; } =
        [MotionSetting.Auto, MotionSetting.Full, MotionSetting.Reduced, MotionSetting.Off];
}
