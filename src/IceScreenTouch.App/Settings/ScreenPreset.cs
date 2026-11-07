using IceScreenTouch.App.Resources;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Settings;

/// <summary>
/// The shape of the mirrored device's screen (portrait, in points). It matters only when the receiver does not
/// report the picture size: the picture is then looked for as a centered rectangle of this shape (AirServer and
/// other running receivers), and the bridge assumes it until UxPlay reports the size. Models of one family share
/// a shape closely enough (the picture locator allows 3%).
/// </summary>
/// <param name="Id">Stored in settings.json.</param>
/// <param name="LabelName">Name of the label in <see cref="Strings"/>.</param>
/// <param name="Width">Portrait width in points.</param>
/// <param name="Height">Portrait height in points.</param>
public sealed record ScreenPreset(string Id, string LabelName, int Width, int Height)
{
    /// <summary>iPhone X and later (19.5:9): the shape when nothing else tells.</summary>
    public static ScreenPreset Default { get; } = new("iphone", nameof(Strings.ScreenIphone), 393, 852);

    /// <summary>
    /// Automatic, the default: the shape of the device that gets the input, from the model number it reports over
    /// Bluetooth (<see cref="ForModel"/>); <see cref="Default"/>'s size until then.
    /// </summary>
    public static ScreenPreset Auto { get; } = new("auto", nameof(Strings.ScreenAuto), 393, 852);

    private static readonly ScreenPreset HomeButton = new("iphone-home-button", nameof(Strings.ScreenIphoneHomeButton), 375, 667);
    private static readonly ScreenPreset Ipad = new("ipad", nameof(Strings.ScreenIpad), 820, 1180);
    private static readonly ScreenPreset IpadPro11 = new("ipad-pro-11", nameof(Strings.ScreenIpadPro11), 834, 1194);
    private static readonly ScreenPreset IpadMini = new("ipad-mini", nameof(Strings.ScreenIpadMini), 744, 1133);
    private static readonly ScreenPreset Ipad4x3 = new("ipad-4x3", nameof(Strings.ScreenIpad4x3), 768, 1024);

    /// <summary>Every preset, in the order of the settings form.</summary>
    public static IReadOnlyList<ScreenPreset> All { get; } = [Auto, Default, HomeButton, Ipad, IpadPro11, IpadMini, Ipad4x3];

    /// <summary>This is <see cref="Auto"/>.</summary>
    public bool IsAuto => Id == Auto.Id;

    /// <summary>
    /// The shape of a model number as the device reports it over Bluetooth (Device Information, Model Number), such
    /// as "iPhone16,1" or "iPad13,1"; null for anything else. Seen on the devices: iPhone16,1 (iPhone 15 Pro) and
    /// iPad13,1 (iPad Air 4th generation), 2026-11-06; the rest of the table follows Apple's published model
    /// numbers. Unknown newer iPhones are taken as <see cref="Default"/>, unknown newer iPads as "ipad".
    /// </summary>
    public static ScreenPreset? ForModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        model = model.Trim();
        var family = model.StartsWith("iPhone", StringComparison.Ordinal) ? "iPhone"
            : model.StartsWith("iPod", StringComparison.Ordinal) ? "iPod"
            : model.StartsWith("iPad", StringComparison.Ordinal) ? "iPad"
            : null;
        var numbers = family is null ? [] : model[family.Length..].Split(',');
        if (numbers.Length != 2
            || !int.TryParse(numbers[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(numbers[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        return family switch
        {
            "iPod" => HomeButton,
            "iPhone" => major <= 9 || (major, minor) is (10, 1) or (10, 2) or (10, 4) or (10, 5) or (12, 8) or (14, 6) ? HomeButton : Default,
            _ => (major, minor) switch
            {
                (14, 1 or 2) or (16, 1 or 2) => IpadMini, // mini 6th gen, mini A17 Pro
                (8, >= 1 and <= 4) or (8, 9 or 10) or (13, >= 4 and <= 7) or (14, 3 or 4) or (16, 3 or 4) => IpadPro11,
                (13, 1 or 2) or (13, >= 16 and <= 19) or (14, 8 or 9) or (15, 3 or 4) or (15, 7 or 8) => Ipad, // Air 11-inch, iPad 10th gen and A16
                (13, >= 8 and <= 11) or (14, 5 or 6) or (14, 10 or 11) or (15, 5 or 6) or (16, 5 or 6) => Ipad4x3, // 12.9 and 13-inch
                _ when major <= 12 => Ipad4x3, // models with a Home button, and older Pros
                _ => Ipad,
            },
        };
    }

    /// <summary>Shown in the settings form, in the user's language.</summary>
    public string Label => Strings.ResourceManager.GetString(LabelName, Strings.Culture) ?? Id;

    /// <summary>The portrait size as the core type.</summary>
    public SizeD Size => new(Width, Height);

    /// <summary>Width / height in portrait.</summary>
    public double PortraitAspect => (double)Width / Height;

    /// <summary>The preset with <paramref name="id"/>, or null.</summary>
    public static ScreenPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The combo box shows this.</summary>
    public override string ToString() => Label;
}
