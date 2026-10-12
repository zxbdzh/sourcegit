using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // 仅供固定的 AI 只读工具使用；参数列表不可直接暴露给模型。
    internal static class GitReadOnlyForAI
    {
        internal static async Task<Command.Result> ReadAsync(string repo, IReadOnlyList<string> args, CancellationToken cancellation, int limit = 80000)
        {
            var start = new ProcessStartInfo(Native.OS.GitExecutable)
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Git 配置和参数均由工具代码固定，避免查询触发索引写入或外部命令。
            foreach (var argument in new[] { "--no-pager", "--no-optional-locks", "--no-replace-objects", "--literal-pathspecs", "-c", "core.quotepath=false", "-c", "core.fsmonitor=false", "-c", "credential.helper=", "-c", "diff.external=" })
                start.ArgumentList.Add(argument);
            foreach (var argument in args)
                start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_NO_LAZY_FETCH"] = "1";

            using var process = new Process { StartInfo = start };
            cancellation.ThrowIfCancellationRequested();
            process.Start();
            using var registration = cancellation.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { }
            });
            var stdout = ReadBoundedAsync(process.StandardOutput, limit, cancellation);
            var stderr = ReadBoundedAsync(process.StandardError, 4000, cancellation);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return new Command.Result
            {
                IsSuccess = process.ExitCode == 0,
                StdOut = await stdout.ConfigureAwait(false),
                StdErr = await stderr.ConfigureAwait(false),
            };
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellation)
        {
            var result = new StringBuilder();
            var buffer = new char[4096];
            var truncated = false;
            int count;
            // 达到返回预算后仍排空管道，避免 Git 因消费者停止读取而死锁。
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
            {
                var remaining = Math.Max(0, limit - result.Length);
                result.Append(buffer, 0, Math.Min(remaining, count));
                truncated |= count > remaining;
            }
            if (truncated)
                result.Append("\n[Result truncated; narrow the path or range.]");
            return result.ToString();
        }
    }
}
