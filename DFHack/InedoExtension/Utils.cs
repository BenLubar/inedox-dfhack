using Inedo.Agents;
using Inedo.Extensibility.Operations;
using Inedo.Extensions.DFHack.VariableFunctions;
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

        public static async Task WrapInBuildEnvAsync(this RemoteProcessStartInfo info, IOperationExecutionContext context, string imageName, bool shareCache, params string[] additionalPaths)
        {
            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();
            var executionBaseDir = await context.GetExecutionBaseDirAsync();

            var volumes = string.Join(" ",
                new[] { executionBaseDir }.Concat(additionalPaths)
                .Select(s => fileOps.CombinePath(info.WorkingDirectory, s))
                .Select(s => "-v " + $"{s}:{s}".EscapeLinuxArg())
                .Concat(new[]
                {
                    "-v " + string.Format("{0}:{0}:ro", (await DFHackCacheVariableFunction.GetAsync(context))).EscapeLinuxArg(),
                    shareCache ? "-v \"$HOME/.ccache:$HOME/.ccache\"" : $"-v {fileOps.CombinePath(executionBaseDir, ".ccache").EscapeLinuxArg()}\":$HOME/.ccache\""
                })
            );

            info.Arguments = $"run --rm -e CCACHE_BASEDIR={executionBaseDir.EscapeLinuxArg()} {volumes} -w {info.WorkingDirectory.EscapeLinuxArg()} -e CCACHE_DIR=\"$HOME/.ccache\" -u $(id -u):$(id -g) {imageName.EscapeLinuxArg()} {info.FileName.EscapeLinuxArg()} {info.Arguments}";
            info.FileName = "/usr/bin/docker";
        }
    }
}
