using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Inedo.Agents;
using Inedo.Extensibility.Operations;
using Inedo.Extensions.DFHack.VariableFunctions;

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

            // XXX: assumes standardized execution directory layout
            return fileOps.CombinePath(baseDir, $"_E{context.ExecutionId}");
        }

        public static async Task<string> WrapInBuildEnvAsync(this RemoteProcessStartInfo info, IOperationExecutionContext context, string imageName, bool shareCache, bool allowNetwork = false, bool forceASLR = true, params string[] additionalPaths)
        {
            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();
            var executionBaseDir = await context.GetExecutionBaseDirAsync();

            var volumes = string.Join(" ",
                new[] { executionBaseDir }.Concat(additionalPaths)
                .Select(s => fileOps.CombinePath(info.WorkingDirectory, s))
                .Select(s => "-v " + $"{s}:{s.Replace($"_E{context.ExecutionId}", "_E0")}".EscapeLinuxArg())
                .Concat(new[]
                {
                    "-v " + string.Format("{0}:{0}:ro", (await DFHackCacheVariableFunction.GetAsync(context))).EscapeLinuxArg(),
                    shareCache ? "-v \"/home/buildmaster/.ccache:/home/buildmaster/.ccache\"" : $"-v \"/home/buildmaster/.ccache:/home/buildmaster/.ccache:ro\" -e CCACHE_READONLY=1 -e CCACHE_TEMPDIR=\"/home/buildmaster/.ccache_temp\" -v {fileOps.CombinePath(executionBaseDir, ".ccache").EscapeLinuxArg()}\":/home/buildmaster/.ccache_temp\""
                })
            );

            var network = allowNetwork ? string.Empty : "--network none";
            var security = string.Empty;
            if (!forceASLR)
            {
                // Store seccomp.json outside of the execution directory root so the image cannot modify it.
                var seccompPath = fileOps.CombinePath(executionBaseDir, "..", "docker-seccomp.json");
                using (var output = await fileOps.OpenFileAsync(seccompPath, FileMode.Create, FileAccess.Write))
                using (var input = typeof(Utils).Assembly.GetManifestResourceStream("Inedo.Extensions.DFHack.docker-seccomp.json"))
                {
                    await input.CopyToAsync(output);
                }
                security = "--security-opt seccomp=" + seccompPath.EscapeLinuxArg();
            }

            var env = string.Join(" ", info.EnvironmentVariables
                .Concat(new[] { new KeyValuePair<string, string>("CCACHE_BASEDIR", executionBaseDir.Replace($"_E{context.ExecutionId}", "_E0")) })
                .Select(kv => $"-e {kv.Key.EscapeLinuxArg()}={kv.Value.EscapeLinuxArg()}"));
            info.EnvironmentVariables.Clear();

            var cidfile = fileOps.CombinePath(executionBaseDir, "cid-" + Guid.NewGuid().ToString("N"));

            info.Arguments = $"run --rm {env} {volumes} {network} {security} --cidfile {cidfile.EscapeLinuxArg()} -w {info.WorkingDirectory.Replace($"_E{context.ExecutionId}", "_E0").EscapeLinuxArg()} -e CCACHE_DIR=\"/home/buildmaster/.ccache\" -u $(id -u):$(id -g) --memory=16g --memory-swap=16g {imageName.EscapeLinuxArg()} {info.FileName.EscapeLinuxArg()} {info.Arguments}";
            info.FileName = "/usr/bin/docker";

            return cidfile;
        }
    }
}
