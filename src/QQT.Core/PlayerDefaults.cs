namespace QQT.Core;

/// <summary>由宿主配置提供的初始属性；核心不为生产对局补充隐藏默认数值。</summary>
public readonly record struct PlayerDefaults(int Speed, int Capacity, int Range, int Vision, RationalTime Lifetime)
{
    public void Validate()
    {
        if (Speed <= 0 || Capacity <= 0 || Range <= 0 || Vision < 0 || Lifetime <= 0)
            throw new InvalidDataException("初始速度、容量、火力、寿命必须为正，视野半径不能为负。");
    }
}
