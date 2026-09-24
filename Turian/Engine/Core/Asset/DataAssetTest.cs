namespace Turian.Engine.Core;

/// <summary> </summary>
[CreateAssetMenu(fileName: "DataAssetTest", path: "DA/DataAssetTest")]
//[CreateAssetMenu("DA/DataAssetTest")]
[TypeId("a3000000-0000-4000-8000-000000000007")]
public class DataAssetTest : DataAsset
{
    /// <summary> </summary>
    public bool Bool = true;
    /// <summary> </summary>
    public byte Byte = 255;
    /// <summary> </summary>
    public sbyte Sbyte = -128;
    /// <summary> </summary>
    public short Short = -32768;
    /// <summary> </summary>
    public ushort Ushort = 65535;
    /// <summary> </summary>
    public int Int = -2147483648;
    /// <summary> </summary>
    public uint UintNumericUpDown = 4294967295;
    /// <summary> </summary>
    public long Long = -9223372036854775808;
    /// <summary> </summary>
    public ulong Ulong = ulong.MaxValue;
    /// <summary> </summary>
    public float Float = 3.1415927f;
    /// <summary> </summary>
    public double Double = 3.141592653589793;
    /// <summary> </summary>
    public decimal Decimal = 123456789.0123456789012345678m;
}
