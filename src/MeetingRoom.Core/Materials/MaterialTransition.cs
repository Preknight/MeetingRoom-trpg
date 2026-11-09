namespace MeetingRoom.Core.Materials;

/// <summary>
/// 一次状态转移的结果。<see cref="Applied"/> 为 false 时 <see cref="Rejection"/> 说明为什么被拒——
/// 非法转移不是异常，而是可预期的领域结果。
/// </summary>
public sealed record MaterialTransition(bool Applied, MaterialCard? Card, string? Rejection)
{
    public static MaterialTransition Rejected(string reason) => new(false, null, reason);

    public static MaterialTransition Changed(MaterialCard card) => new(true, card, null);
}
