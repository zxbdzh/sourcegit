using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace SourceGit.AI
{
    public class Agent
    {
        public Agent(Service service)
        {
            _service = service;
        }


        public async Task<string> ChatAsync(List<ChatMessage> messages, Action<string> onDelta, string repo, CancellationToken cancellation, Action<ToolCallProgress> onToolProgress = null, Action<ViewModels.AIChatBaseline> onBaseline = null, Action<string> onCompatibility = null)
        {
            var chatClient = _service.GetChatClient();
            if (chatClient == null)
                throw new Exception("Failed to fetch available models from this service. Please check your configuration and try again.");

            var options = new ChatCompletionOptions();
            options.Tools.Add(ChatTools.ReadFile);
            options.Tools.Add(ChatTools.Grep);
            foreach (var tool in GitChatTools.All)
                options.Tools.Add(tool);
#pragma warning disable OPENAI001
            if (!_service.ReasoningEffortLevel.Equals(Options.IgnoredReasoningEffortLevel, StringComparison.OrdinalIgnoreCase))
                options.ReasoningEffortLevel = new ChatReasoningEffortLevel(_service.ReasoningEffortLevel);
#pragma warning restore OPENAI001

            var useStreaming = true;
            var log = new StringBuilder();
            var answer = new StringBuilder();
            void Publish()
            {
                if (onDelta == null)
                    return;
                if (log.Length == 0)
                    onDelta(answer.ToString());
                else if (answer.Length == 0)
                    onDelta(log.ToString());
                else
                    onDelta(log + "\n" + answer);
            }

            for (var round = 0; round < 8; round++)
            {
                answer.Clear();
                ChatFinishReason? finish = null;
                var drafts = new Dictionary<int, ToolDraft>();

                if (useStreaming)
                {
                    try
                    {
                        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellation))
                        {
                            if (update.FinishReason != null)
                                finish = update.FinishReason;
                            foreach (var part in update.ContentUpdate)
                            {
                                if (string.IsNullOrEmpty(part.Text))
                                    continue;
                                answer.Append(part.Text);
                                Publish();
                            }
                            foreach (var tool in update.ToolCallUpdates)
                            {
                                if (!drafts.TryGetValue(tool.Index, out var draft))
                                {
                                    draft = new ToolDraft();
                                    drafts[tool.Index] = draft;
                                }
                                if (!string.IsNullOrEmpty(tool.ToolCallId))
                                    draft.Id = tool.ToolCallId;
                                if (!string.IsNullOrEmpty(tool.FunctionName))
                                    draft.Name = tool.FunctionName;
                                if (tool.FunctionArgumentsUpdate != null)
                                    draft.Args.Append(tool.FunctionArgumentsUpdate.ToString());
                            }
                        }
                    }
                    catch (Exception ex) when (IsResponseFormatError(ex))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // 本轮工具尚未执行；丢弃残缺流，完整响应仍保留工具能力，不静默变成纯文本模式。
                        useStreaming = false;
                        onCompatibility?.Invoke(App.Text("AI.Sidebar.NonStreaming"));
                    }
                if (useStreaming && finish == null)
                {
                    // 部分中转对 tool_calls:null 只返回半截流而不抛异常；没有完成原因就不能把残片当成最终回答。
                    useStreaming = false;
                    answer.Clear();
                    drafts.Clear();
                    onCompatibility?.Invoke(App.Text("AI.Sidebar.NonStreaming"));
                }
                }
                if (!useStreaming)
                {
                    answer.Clear();
                    drafts.Clear();
                    Publish();
                    ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellation);
                    finish = completion.FinishReason;
                    foreach (var part in completion.Content)
                    {
                        if (!string.IsNullOrEmpty(part.Text))
                            answer.Append(part.Text);
                    }
                    if (completion.ToolCalls != null && completion.ToolCalls.Count > 0)
                    {
                        for (int i = 0; i < completion.ToolCalls.Count; i++)
                        {
                            var tc = completion.ToolCalls[i];
                            var draft = new ToolDraft { Id = tc.Id, Name = tc.FunctionName };
                            draft.Args.Append(tc.FunctionArguments.ToString());
                            drafts[i] = draft;
                        }
                    }
                    Publish();
                }

                if (finish == ChatFinishReason.ToolCalls || drafts.Count > 0)
                {
                    var calls = new List<ChatToolCall>();
                    foreach (var draft in drafts.Values)
                    {
                        if (string.IsNullOrEmpty(draft.Id) || string.IsNullOrEmpty(draft.Name))
                            continue;
                        calls.Add(ChatToolCall.CreateFunctionToolCall(draft.Id, draft.Name, BinaryData.FromString(draft.Args.ToString())));
                    }

                    if (calls.Count == 0)
                        throw new Exception("The model requested a tool call without arguments.");

                    var assistant = new AssistantChatMessage(calls);
                    if (answer.Length > 0)
                        assistant.Content.Add(ChatMessageContentPart.CreateTextPart(answer.ToString()));
                    messages.Add(assistant);
                    // 工具日志不进入模型历史；补丁只在回答完成后进入预览集合。
                    if (answer.Length > 0)
                        log.Append(answer).AppendLine().AppendLine();
                    answer.Clear();
                    foreach (var call in calls)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var result = await ChatTools.ProcessWithProgressAsync(call, repo, onToolProgress, cancellation, onBaseline);
                        messages.Add(result);
                    }

                    continue;
                }

                if (finish == ChatFinishReason.Length)
                    throw new Exception("The response was cut off because it reached the maximum length.");
                if (finish == ChatFinishReason.ContentFilter)
                    throw new Exception("The response was omitted due to a content filter flag.");
                if (finish is not (null or ChatFinishReason.Stop))
                    throw new Exception($"Unexpected chat completion status: {finish}.");

                var response = answer.ToString().Trim();
                if (string.IsNullOrEmpty(response))
                    throw new Exception("The model returned an empty response.");

                messages.Add(new AssistantChatMessage(response));
                return log.Length == 0 ? response : (log + "\n" + response).Trim();
            }

            throw new Exception("The model exceeded the tool-call limit.");
        }

        public static bool IsResponseFormatError(Exception error) =>
            error is System.Text.Json.JsonException ||
            error is InvalidOperationException && error.Message.Contains("requires an element of type", StringComparison.Ordinal);

        private sealed class ToolDraft
        {
            public string Id;
            public string Name;
            public StringBuilder Args { get; } = new();
        }

        public async Task GenerateCommitMessageAsync(string repo, string currentBranch, string changeList, string amendParent, Action<string> onUpdate, CancellationToken cancellation)
        {
            var chatClient = _service.GetChatClient();
            if (chatClient == null)
                throw new Exception("Failed to fetch available models from this service. Please check your configuration and try again.");

            var options = new ChatCompletionOptions() { Tools = { ChatTools.GetDetailChangesInFile } };
#pragma warning disable OPENAI001
            if (!_service.ReasoningEffortLevel.Equals(Options.IgnoredReasoningEffortLevel, StringComparison.OrdinalIgnoreCase))
                options.ReasoningEffortLevel = new ChatReasoningEffortLevel(_service.ReasoningEffortLevel);
#pragma warning restore OPENAI001

            var userMessageBuilder = new StringBuilder();
            userMessageBuilder
                .AppendLine("Generate a commit message (follow the rule of conventional commit message) for given git repository.")
                .AppendLine("- Read all given changed files before generating. Only binary files (such as images, audios ...) can be skipped.")
                .AppendLine("- Output the conventional commit message (with detail changes in list) directly. Do not explain your output nor introduce your answer.")
                .AppendLine(_service.AdditionalPrompt)
                .Append("Repository path: ").AppendLine(repo.Quoted())
                .Append("Current branch: ").AppendLine(currentBranch.Quoted())
                .AppendLine("Changed files ('A' means added, 'M' means modified, 'D' means deleted, 'T' means type changed, 'R' means renamed, 'C' means copied): ")
                .Append(changeList);

            var messages = new List<ChatMessage>() { new UserChatMessage(userMessageBuilder.ToString()) };

            do
            {
                ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellation);
                var inProgress = false;

                switch (completion.FinishReason)
                {
                    case ChatFinishReason.Stop:
                        if (onUpdate != null)
                        {
                            onUpdate.Invoke(string.Empty);
                            onUpdate.Invoke("# Assistant");
                            if (completion.Content.Count > 0)
                            {
                                var text = completion.Content[0].Text.ReplaceLineEndings("\n").Trim();
                                var start = 0;
                                var len = text.Length;
                                if (text.StartsWith("```", StringComparison.Ordinal))
                                {
                                    var idx = text.IndexOf('\n') + 1;
                                    start += idx;
                                    len -= idx;
                                }

                                if (text.EndsWith("\n```", StringComparison.Ordinal))
                                    len -= 4;

                                if (len > 0)
                                    onUpdate.Invoke(text.Substring(start, len));
                                else
                                    onUpdate.Invoke("[No content was generated.]");
                            }
                            else
                            {
                                onUpdate.Invoke("[No content was generated.]");
                            }

                            onUpdate.Invoke(string.Empty);
                            onUpdate.Invoke("# Token Usage");
                            onUpdate.Invoke($"Total: {completion.Usage.TotalTokenCount}. Input: {completion.Usage.InputTokenCount}. Output: {completion.Usage.OutputTokenCount}");
                        }
                        break;
                    case ChatFinishReason.Length:
                        throw new Exception("The response was cut off because it reached the maximum length. Consider increasing the max tokens limit.");
                    case ChatFinishReason.ToolCalls:
                        {
                            var message = new AssistantChatMessage(completion);
#pragma warning disable SCME0001
                            var hasReasoningContent = completion.Patch.TryGetValue("$.choices[0].message.reasoning_content"u8, out string reasoning);
                            if (hasReasoningContent)
                            {
                                if (string.IsNullOrEmpty(reasoning))
                                    message.Patch.Set("$.reasoning_content"u8, BinaryData.FromString("\"\""));
                                else
                                    message.Patch.Set("$.reasoning_content"u8, reasoning);
                            }
#pragma warning restore SCME0001
                            messages.Add(message);

                            foreach (var call in completion.ToolCalls)
                            {
                                var result = await ChatTools.ProcessAsync(call, repo, amendParent, onUpdate);
                                messages.Add(result);
                            }

                            inProgress = true;
                            break;
                        }
                    case ChatFinishReason.ContentFilter:
                        throw new Exception("Omitted content due to a content filter flag");
                    default:
                        break;
                }

                if (!inProgress)
                    break;
            } while (true);
        }

        private readonly Service _service;
    }
}
