using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class GrepForAI : Command
    {
        public GrepForAI(string repo, string pattern, string path)
        {
            WorkingDirectory = repo;
            RaiseError = false;
            // 模型参数作为独立 argv；--literal-pathspecs 禁止把路径当 Git 查询表达式。
            _arguments = ["grep", "-n", "-I", "-E", "--untracked", "--max-count=50", "-e", pattern, "--"];
            if (!string.IsNullOrEmpty(path))
                _arguments.Add(path);
        }

        public async Task<Result> ReadAsync()
        {
            return await GitReadOnlyForAI.ReadAsync(WorkingDirectory, _arguments, CancellationToken, 30000).ConfigureAwait(false);
        }

        private readonly List<string> _arguments;
    }
}
