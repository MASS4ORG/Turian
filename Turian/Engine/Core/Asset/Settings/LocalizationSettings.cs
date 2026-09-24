namespace Turian.Engine.Core;

/// <summary>
/// The project's localization configuration: which locale the game starts in and which locales it
/// ships. Defaults to English with English as the only available locale, so a project that never
/// authors this asset still resolves keys through the <c>en</c> table.
/// </summary>
[CreateAssetMenu(fileName: "LocalizationSettings", path: "Settings/Localization Settings")]
[TypeId("a3000005-0000-4000-8000-000000000004")]
public class LocalizationSettings : ProjectSettingsAsset
{
    /// <summary>The locale the game starts in and unresolved keys fall back to.</summary>
    public string DefaultLocale { get; set; } = "en";

    /// <summary>The BCP-47 locales the game ships and offers in its language picker.</summary>
    public List<string> AvailableLocales { get; set; } = ["en"];
}
