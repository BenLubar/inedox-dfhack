using Inedo.Agents;
using Inedo.Extensibility.Operations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Inedo.Extensions.DFHack
{
    internal static class Utils
    {
        public static string EscapeLinuxArg(this string arg)
        {
            if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '_' || c == '.'))
            {
                return arg;
            }

            return "'" + arg.Replace("'", "'\\''") + "'";
        }

        public static string EscapeWindowsArg(this string arg)
        {
            // https://msdn.microsoft.com/en-us/library/ms880421

            if (!arg.Any(c => char.IsWhiteSpace(c) || c == '\\' || c == '"'))
            {
                return arg;
            }

            var str = new StringBuilder();
            str.Append('"');
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '"')
                {
                    str.Append('\\', slashes);
                    str.Append('\\', '"');
                    slashes = 0;
                }
                else if (c == '\\')
                {
                    str.Append('\\');
                    slashes++;
                }
                else
                {
                    str.Append(c);
                    slashes = 0;
                }
            }
            str.Append('\\', slashes);
            str.Append('"');

            return str.ToString();
        }

        public static async Task<string> GetExecutionBaseDirAsync(this IOperationExecutionContext context)
        {
            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();
            var baseDir = await fileOps.GetBaseWorkingDirectoryAsync();

            // XXX: assumes standardized execution directory layout and no deployables
            return fileOps.CombinePath(baseDir, $"_E{context.ExecutionId}", "_D0");
        }

        public static async Task AddCCacheAsync(this RemoteProcessStartInfo info, IOperationExecutionContext context)
        {
            var executionBaseDir = await context.GetExecutionBaseDirAsync();

            info.Arguments = $"PATH=\"/usr/lib/ccache:$PATH\" CCACHE_BASEDIR={Utils.EscapeLinuxArg(executionBaseDir)} CCACHE_SLOPPINESS={Utils.EscapeLinuxArg("file_macro,include_file_ctime,include_file_mtime")} {info.FileName} {info.Arguments}";
            info.FileName = "/usr/bin/env";
        }

        public static async Task WrapInMacGCCAsync(this RemoteProcessStartInfo info, IOperationExecutionContext context)
        {
            var executionBaseDir = await context.GetExecutionBaseDirAsync();
            var gitCacheDir = (await context.ExpandVariablesAsync("$DFHackGitCache")).AsString();

            info.Arguments = info.Arguments.Replace("PATH=\"/usr/lib/ccache:$PATH\" ", "");
            info.Arguments = $"run --rm -i -v {Utils.EscapeLinuxArg($"{executionBaseDir}:{executionBaseDir}")} -v {Utils.EscapeLinuxArg($"{gitCacheDir}:{gitCacheDir}:ro")} -v \"$HOME/.ccache:$HOME/.ccache\" -e CCACHE_DIR=\"$HOME/.ccache\" -u $(id -u):$(id -g) -w {Utils.EscapeLinuxArg(context.WorkingDirectory)} benlubar/macgcc {info.FileName} {info.Arguments}";
            info.FileName = "/usr/bin/docker";
        }
    }
}
