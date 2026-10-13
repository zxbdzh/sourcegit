using System;

namespace SourceGit.AI
{
    public enum ToolCallState
    {
        Running,
        Completed,
        Failed,
        TimedOut,
        Canceled,
    }

    // 工具进度只用于展示，不能作为额外消息写回模型历史。
    public sealed record ToolCallProgress(string Id, string Name, string Arguments, ToolCallState State, TimeSpan Elapsed, string Result = "", string SummaryScope = "", string SummaryText = "");
}
