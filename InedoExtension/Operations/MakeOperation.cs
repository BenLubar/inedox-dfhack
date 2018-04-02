using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;

namespace Inedo.Extensions.DFHack.Operations
{
    [DisplayName("[DFHack] Run Make")]
    [Description("Equivalent to running the \"make\" command, but with better progress reporting.")]
    [ScriptNamespace("DFHack", PreferUnqualified = false)]
    [ScriptAlias("Make")]
    public sealed class MakeOperation : ExecuteOperation
    {
        [DisplayName("Targets")]
        [ScriptAlias("Targets")]
        public IEnumerable<string> Targets { get; set; }

        [DisplayName("Use ccache")]
        [ScriptAlias("UseCCache")]
        public bool UseCCache { get; set; }

        [DisplayName("Parallelism")]
        [ScriptAlias("Parallelism")]
        [PlaceholderText("[use all available processors]")]
        public int? Parallelism { get; set; }

        [DisplayName("Mac Build")]
        [ScriptAlias("MacBuild")]
        public bool MacBuild { get; set; }

        private volatile OperationProgress progress = null;
        public override OperationProgress GetProgress() => this.progress;

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var execOps = await context.Agent.GetServiceAsync<IRemoteProcessExecuter>();
            int? processorCount = this.Parallelism;
            if (!processorCount.HasValue)
            {
                this.LogDebug("Using nproc to determine available processor count...");

                try
                {
                    using (var nproc = execOps.CreateProcess(new RemoteProcessStartInfo
                    {
                        FileName = "/usr/bin/nproc"
                    }))
                    {
                        nproc.OutputDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                            {
                                processorCount = processorCount ?? AH.ParseInt(e.Data);
                            }
                        };

                        nproc.ErrorDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                            {
                                this.LogWarning(e.Data);
                            }
                        };

                        nproc.Start();

                        await nproc.WaitAsync(context.CancellationToken);
                    }
                }
                catch
                {
                }

                if (!processorCount.HasValue)
                {
                    this.LogWarning("Unable to determine processor count!");
                    processorCount = 1;
                }
            }

            this.LogDebug($"Using {processorCount} parallel jobs.");
            this.LogDebug($"Running in directory: {context.WorkingDirectory}");

            var makeStartInfo = new RemoteProcessStartInfo
            {
                FileName = "/usr/bin/make",
                WorkingDirectory = context.WorkingDirectory,
                Arguments = $"-j{processorCount} -- {string.Join(" ", this.Targets.Select(EscapeLinuxArg))}"
            };

            this.LogInformation($"Running make with arguments: {makeStartInfo.Arguments}");

            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();
            var baseDir = await fileOps.GetBaseWorkingDirectoryAsync();

            // XXX: assumes standardized execution directory layout and no deployables
            var executionBaseDir = fileOps.CombinePath(baseDir, $"_E{context.ExecutionId}", "_D0");

            if (this.UseCCache)
            {
                this.LogDebug($"Using ccache base directory: {executionBaseDir}");

                makeStartInfo.Arguments = $"CCACHE_BASEDIR={EscapeLinuxArg(executionBaseDir)} CCACHE_SLOPPINESS={EscapeLinuxArg("file_macro")} make {makeStartInfo.Arguments}";
                makeStartInfo.FileName = "/usr/bin/env";
                if (!this.MacBuild)
                {
                    makeStartInfo.Arguments = $"PATH=\"/usr/lib/ccache:$PATH\" {makeStartInfo.Arguments}";
                }

                this.LogDebug($"Adjusted command for ccache: {makeStartInfo.FileName} {makeStartInfo.Arguments}");
            }

            if (this.MacBuild)
            {
                var gitCacheDir = (await context.ExpandVariablesAsync("$DFHackGitCache")).AsString();

                makeStartInfo.Arguments = $"run --rm -i -v {EscapeLinuxArg($"{executionBaseDir}:{executionBaseDir}")} -v {EscapeLinuxArg($"{gitCacheDir}:{gitCacheDir}:ro")}{(this.UseCCache ? "-v \"$HOME/.ccache:$HOME/.ccache\" -e CCACHE_DIR=\"$HOME/.ccache\"" : string.Empty)} -u $(id -u):$(id -g) -w {EscapeLinuxArg(context.WorkingDirectory)} benlubar/macgcc {makeStartInfo.FileName} {makeStartInfo.Arguments}";
                makeStartInfo.FileName = "/usr/bin/docker";

                this.LogDebug($"Adjusted command for Mac build: {makeStartInfo.FileName} {makeStartInfo.Arguments}");
            }

            using (var make = execOps.CreateProcess(makeStartInfo))
            {
                this.progress = new OperationProgress(0);

                var activeTargets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                make.OutputDataReceived += (s, e) =>
                {
                    int? percentage = null;
                    bool targetsChanged = false;

                    if (e.Data.Length > "[100%] ".Length && e.Data[0] == '[' && e.Data[4] == '%' && e.Data[5] == ']' && e.Data[6] == ' ')
                    {
                        percentage = AH.ParseInt(e.Data.Substring(1, 3).TrimStart());
                        if (e.Data.Substring("[100%] ".Length).StartsWith("Built target "))
                        {
                            if (activeTargets.Remove(e.Data.Substring("[100%] Built target ".Length).Trim()))
                            {
                                targetsChanged = true;
                            }
                        }
                    }
                    else if (e.Data.StartsWith("Scanning dependencies of target "))
                    {
                        if (activeTargets.Add(e.Data.Substring("Scanning dependencies of target ".Length).Trim()))
                        {
                            targetsChanged = true;
                        }
                    }
                    else if (e.Data.StartsWith("CPack: "))
                    {
                        this.LogInformation(e.Data);
                        if (!e.Data.StartsWith("CPack: - "))
                        {
                            this.progress = new OperationProgress(100, e.Data);
                        }
                        return;
                    }

                    if (targetsChanged || (percentage.HasValue && percentage != this.progress.Percent))
                    {
                        this.progress = new OperationProgress(percentage ?? this.progress.Percent, string.Join(", ", activeTargets));
                    }

                    if (targetsChanged && percentage.HasValue)
                    {
                        this.LogInformation(e.Data);
                    }
                    else
                    {
                        this.LogDebug(e.Data);
                    }
                };

                make.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data.Contains(": error: "))
                    {
                        this.LogError(e.Data);
                    }
                    else
                    {
                        this.LogWarning(e.Data);
                    }
                };

                make.Start();
                await make.WaitAsync(context.CancellationToken);

                if (make.ExitCode == 0)
                {
                    this.LogDebug("make exited with code 0 (success)");
                }
                else if (make.ExitCode.HasValue)
                {
                    this.LogError($"make exited with code {make.ExitCode} (failure)");
                }
                else
                {
                    this.LogError("make exited with unknown code");
                }
            }
        }

        private static string EscapeLinuxArg(string arg)
        {
            if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '_' || c == '.'))
            {
                return arg;
            }

            return "'" + arg.Replace("'", "'\\''") + "'";
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            var details = new RichDescription();
            if (string.Equals(config[nameof(MacBuild)], "true", StringComparison.OrdinalIgnoreCase))
            {
                details.AppendContent("for Mac OS X ");
            }
            else
            {
                details.AppendContent("for Linux ");
            }
            if (string.Equals(config[nameof(UseCCache)], "true", StringComparison.OrdinalIgnoreCase))
            {
                details.AppendContent("using ccache ");
            }
            if (!string.IsNullOrEmpty(config[nameof(Parallelism)]))
            {
                details.AppendContent("with up to ", new Hilite(config[nameof(Parallelism)]), " parallel jobs");
            }
            else
            {
                details.AppendContent("on all available processor cores");
            }

            return new ExtendedRichDescription(
                new RichDescription("Make ", new ListHilite(config[nameof(Targets)].AsEnumerable())),
                details
            );
        }
    }
}
