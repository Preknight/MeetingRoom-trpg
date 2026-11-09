namespace MeetingRoom.Core.Materials;

/// <summary>资料审核决策（设计文档 §4.2 的六态两级审批）。</summary>
public enum ReviewDecision
{
    /// <summary>draft → host_review（作者提交；正常路径由 <c>SubmitAsync</c> 一次完成）。</summary>
    Submit,

    /// <summary>host_review → user_review（主持人通过）。</summary>
    Approve,

    /// <summary>host_review | user_review → rejected。</summary>
    Reject,

    /// <summary>user_review → adopted。<b>只有用户能做</b>——主持人不得越过用户直接采纳。</summary>
    Adopt,

    /// <summary>user_review → parked（用户搁置，保留在列表里可重新激活）。</summary>
    Park,

    /// <summary>rejected | parked → draft（重新激活，可修改后重提）。</summary>
    Reopen,
}

/// <summary>谁在发起审核动作，决定权限边界：主持人只能"通过/驳回"，采纳权在用户。</summary>
public enum ReviewActorKind
{
    /// <summary>普通与会者（只能提交自己的资料、重开被驳回的资料）。</summary>
    Member,

    /// <summary>主持人 Agent（只能通过/驳回）。</summary>
    Host,

    /// <summary>用户本人（终审：采纳/打回/搁置）。</summary>
    User,
}
