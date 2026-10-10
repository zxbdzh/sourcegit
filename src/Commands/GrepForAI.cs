using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class GrepForAI : Command
    {
        public GrepForAI(string repo, string pattern, string path)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;

            var builder = new StringBuilder();
            // 工作区文本搜索；-I 跳过二进制，--untracked 包含未跟踪文件。
            builder.Append("grep -n -I -E --untracked --max-count=50 -e ").Append(pattern.Quoted());
            if (!string.IsNullOrEmpty(path))
                builder.Append(" -- ").Append(path.Quoted());
            Args = builder.ToString();
        }

        public async Task<Result> ReadAsync()
        {
            return await ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
