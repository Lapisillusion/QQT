using System.Numerics;
using System.Text.Json;

namespace QQT.Core;

/// <summary>精确分数 tick（亦用于质点坐标）；默认值是 0，显示小数从不参与裁决。</summary>
public readonly struct RationalTime : IEquatable<RationalTime>, IComparable<RationalTime>
{
    private readonly long _denominator;
    public long Numerator { get; }
    public long Denominator => _denominator == 0 ? 1 : _denominator;
    public RationalTime(long numerator, long denominator = 1) : this((BigInteger)numerator, denominator) { }
    private RationalTime(BigInteger numerator, BigInteger denominator)
    {
        if (denominator <= 0) throw new InvalidDataException("分母必须为正。");
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = checked((long)(numerator / gcd));
        _denominator = checked((long)(denominator / gcd));
    }
    public static implicit operator RationalTime(int value) => new(value);
    public static RationalTime operator +(RationalTime a, RationalTime b) => new((BigInteger)a.Numerator * b.Denominator + (BigInteger)b.Numerator * a.Denominator, (BigInteger)a.Denominator * b.Denominator);
    public static RationalTime operator -(RationalTime a, RationalTime b) => new((BigInteger)a.Numerator * b.Denominator - (BigInteger)b.Numerator * a.Denominator, (BigInteger)a.Denominator * b.Denominator);
    public static RationalTime operator *(RationalTime a, int b) => new((BigInteger)a.Numerator * b, a.Denominator);
    public static RationalTime operator /(RationalTime a, int b) => b > 0 ? new(a.Numerator, (BigInteger)a.Denominator * b) : throw new ArgumentOutOfRangeException(nameof(b));
    public int CompareTo(RationalTime other) => ((BigInteger)Numerator * other.Denominator).CompareTo((BigInteger)other.Numerator * Denominator);
    public bool Equals(RationalTime other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is RationalTime value && Equals(value);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public static bool operator ==(RationalTime a, RationalTime b) => a.Equals(b);
    public static bool operator !=(RationalTime a, RationalTime b) => !a.Equals(b);
    public static bool operator <(RationalTime a, RationalTime b) => a.CompareTo(b) < 0;
    public static bool operator >(RationalTime a, RationalTime b) => a.CompareTo(b) > 0;
    public static bool operator <=(RationalTime a, RationalTime b) => a.CompareTo(b) <= 0;
    public static bool operator >=(RationalTime a, RationalTime b) => a.CompareTo(b) >= 0;
    public double ToDouble() => (double)Numerator / Denominator;
    public override string ToString() => Denominator == 1 ? Numerator.ToString() : $"{Numerator}/{Denominator}";
    public static RationalTime Read(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? new(value.GetInt64()) : new(value.GetProperty("numerator").GetInt64(), value.GetProperty("denominator").GetInt64());
    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); writer.WriteNumber("numerator", Numerator); writer.WriteNumber("denominator", Denominator); writer.WriteEndObject();
    }
}
