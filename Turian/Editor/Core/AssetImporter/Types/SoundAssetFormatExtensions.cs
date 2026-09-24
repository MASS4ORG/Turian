namespace Turian.Editor.Core;

static class SoundAssetFormatExtensions
{
    public static SoundAssetFormat FromFilePath(string filePath)
    {
        return Path.GetExtension(filePath).ToUpperInvariant() switch
        {
            ".WAV" => SoundAssetFormat.Wav,
            ".MP3" => SoundAssetFormat.Mp3,
            ".OGG" => SoundAssetFormat.Ogg,
            ".FLAC" => SoundAssetFormat.Flac,
            ".AAC" => SoundAssetFormat.Aac,
            _ => SoundAssetFormat.Unknown
        };
    }
}
