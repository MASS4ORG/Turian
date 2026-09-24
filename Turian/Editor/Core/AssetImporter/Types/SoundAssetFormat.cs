namespace Turian.Editor.Core;

/// <summary>Sound file formats.</summary>
public enum SoundAssetFormat
{
    /// <summary>Unknown or unsupported format.</summary>
    Unknown = 0,

    /// <summary>Waveform Audio File Format.</summary>
    Wav,

    /// <summary>MPEG Audio Layer III.</summary>
    Mp3,

    /// <summary>Ogg Vorbis audio.</summary>
    Ogg,

    /// <summary>Free Lossless Audio Codec.</summary>
    Flac,

    /// <summary>Advanced Audio Coding.</summary>
    Aac
}
