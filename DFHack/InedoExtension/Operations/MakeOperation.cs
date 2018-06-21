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
    [AppliesTo(InedoProduct.BuildMaster)]
    public sealed class MakeOperation : BuildEnvOperationBase
    {
        [DisplayName("Target")]
        [ScriptAlias("Target")]
        public string Target { get; set; }

        private volatile OperationProgress progress = null;
        public override OperationProgress GetProgress() => this.progress;

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var execOps = await context.Agent.GetServiceAsync<IRemoteProcessExecuter>();

            var makeStartInfo = new RemoteProcessStartInfo
            {
                FileName = "dfhack-make",
                Arguments = this.Target.EscapeLinuxArg(),
                WorkingDirectory = context.WorkingDirectory
            };

            await this.LogAndWrapCommandAsync(context, makeStartInfo);

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

                    string text = e.Data;
                    if (this.ImageTag == "msvc")
                    {
                        text = e.Data.TrimEnd();
                        if ((!targetsChanged || !percentage.HasValue) && e.Data != text && new[] { ".c", ".cc", ".cpp" }.Any(ext => text.EndsWith(ext)))
                        {
                            return;
                        }
                        if (text == @"wine: cannot find L""C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\mscorsvw.exe""")
                        {
                            return;
                        }
                    }

                    if (targetsChanged && percentage.HasValue)
                    {
                        this.LogInformation(text);
                    }
                    else
                    {
                        this.LogDebug(text);
                    }
                };

                make.ErrorDataReceived += (s, e) =>
                {
                    string text = e.Data;
                    if (this.ImageTag == "msvc")
                    {
                        text = e.Data.TrimEnd();
                        if (text.StartsWith("cl : Command line warning D9025 : overriding '/O"))
                        {
                            return;
                        }
                        if (text == @"wine: cannot find L""C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\mscorsvw.exe""")
                        {
                            return;
                        }
                    }

                    if (text.Contains(this.ImageTag == "msvc" ? " error C" : ": error: "))
                    {
                        this.LogError(text);
                    }
                    else
                    {
                        this.LogWarning(text);
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

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription("Make ", new Hilite(config[nameof(Target)]))
            );
        }
    }
}
