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
    [ScriptAlias("Make")]
    [AppliesTo(InedoProduct.BuildMaster)]
    public sealed class MakeOperation : BuildEnvOperationBase
    {
        [DisplayName("Target")]
        [ScriptAlias("Target")]
        public string Target { get; set; }

        [DisplayName("Use Ninja instead of Make")]
        [ScriptAlias("UseNinja")]
        public bool UseNinja { get; set; }

        private volatile OperationProgress progress = null;
        public override OperationProgress GetProgress() => this.progress;

        private readonly SortedSet<string> activeTargets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        private string serverName;

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            this.serverName = context.ServerName;

            var execOps = await context.Agent.GetServiceAsync<IRemoteProcessExecuter>();

            var makeStartInfo = new RemoteProcessStartInfo
            {
                FileName = "dfhack-make",
                Arguments = this.Target.EscapeLinuxArg(),
                WorkingDirectory = context.WorkingDirectory
            };

            if (this.UseNinja)
            {
                makeStartInfo.EnvironmentVariables["DFHACK_USE_NINJA"] = "1";
            }

            await this.LogAndWrapCommandAsync(context, makeStartInfo);

            using (var make = execOps.CreateProcess(makeStartInfo))
            {
                this.progress = new OperationProgress(0);

                make.OutputDataReceived += this.OutputDataReceived;
                make.ErrorDataReceived += this.ErrorDataReceived;

                make.Start();
                await make.WaitAsync(context.CancellationToken);

                var processName = this.UseNinja ? "ninja" : "make";

                if (make.ExitCode == 0)
                {
                    this.LogDebug($"{processName} exited with code 0 (success)");
                }
                else if (make.ExitCode.HasValue)
                {
                    this.LogError($"{processName} exited with code {make.ExitCode} (failure)");
                }
                else
                {
                    this.LogError($"{processName} exited with unknown code");
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription("Make ", new Hilite(config[nameof(Target)]))
            );
        }

        private void MakeOutputDataReceived(object source, ProcessDataReceivedEventArgs args)
        {
            int? percentage = null;
            bool targetsChanged = false;

            var line = this.RemoveLogRubbish(args);
            if (line == null)
            {
                return;
            }

            if (line.Length > "[100%] ".Length && line[0] == '[' && line[4] == '%' && line[5] == ']' && line[6] == ' ')
            {
                percentage = AH.ParseInt(line.Substring(1, 3).TrimStart());
                if (line.Substring("[100%] ".Length).StartsWith("Built target "))
                {
                    if (activeTargets.Remove(line.Substring("[100%] Built target ".Length).Trim()))
                    {
                        targetsChanged = true;
                    }
                }
            }
            else if (line.StartsWith("Scanning dependencies of target "))
            {
                if (activeTargets.Add(line.Substring("Scanning dependencies of target ".Length).Trim()))
                {
                    targetsChanged = true;
                }
            }
            else if (line.StartsWith("CPack: "))
            {
                this.LogInformation(line);
                if (!line.StartsWith("CPack: - "))
                {
                    this.progress = new OperationProgress(100, line);
                }
                return;
            }

            if (targetsChanged || (percentage.HasValue && percentage != this.progress.Percent))
            {
                this.progress = new OperationProgress(percentage ?? this.progress.Percent, string.Join(", ", activeTargets));
            }

            if (this.ImageTag == "msvc" && (!targetsChanged || !percentage.HasValue) && args.Data != line && new[] { ".c", ".cc", ".cpp" }.Any(ext => line.EndsWith(ext)))
            {
                return;
            }

            if (targetsChanged && percentage.HasValue)
            {
                this.LogInformation(line);
            }
            else if (line.Contains(this.ImageTag == "msvc" ? " error C" : ": error: "))
            {
                this.LogError(line);
            }
            else if (line.Contains(this.ImageTag == "msvc" ? " warning C" : ": warning: "))
            {
                this.LogWarning(line);
            }
            else
            {
                this.LogDebug(line);
            }
        }

        private void MakeErrorDataReceived(object source, ProcessDataReceivedEventArgs args)
        {
            var line = this.RemoveLogRubbish(args);
            if (line == null)
            {
                return;
            }

            if (line.Contains(this.ImageTag == "msvc" ? " error C" : ": error: "))
            {
                this.LogError(line);
            }
            else
            {
                this.LogWarning(line);
            }
        }

        private void NinjaOutputDataReceived(object source, ProcessDataReceivedEventArgs args)
        {
            int? percentage = null;

            var line = this.RemoveLogRubbish(args);
            if (line == null)
            {
                return;
            }

            var slash = line.IndexOf('/');
            var close = line.IndexOf(']');
            if (slash != -1 && slash < close && line[0] == '[')
            {
                var num = AH.ParseInt(line.Substring(1, slash - 1));
                var den = AH.ParseInt(line.Substring(slash + 1, close - slash - 1));

                if (num.HasValue && den.HasValue)
                {
                    percentage = 100 * num.Value / den.Value;
                    this.progress = new OperationProgress(percentage, $"{this.serverName} {line.Substring(0, close + 1)}");
                }
            }

            if (this.ImageTag == "msvc" && !percentage.HasValue && args.Data != line && new[] { ".c", ".cc", ".cpp" }.Any(ext => line.EndsWith(ext)))
            {
                return;
            }

            if (percentage.HasValue)
            {
                this.LogInformation(line);
            }
            else if (line.Contains(this.ImageTag == "msvc" ? " error C" : ": error: "))
            {
                this.LogError(line);
            }
            else if (line.Contains(this.ImageTag == "msvc" ? " warning C" : ": warning: "))
            {
                this.LogWarning(line);
            }
            else
            {
                this.LogDebug(line);
            }
        }

        private void NinjaErrorDataReceived(object source, ProcessDataReceivedEventArgs args)
        {
            var line = this.RemoveLogRubbish(args);
            if (line == null)
            {
                return;
            }
            if (this.ImageTag == "msvc" && line.StartsWith("cl : Command line warning D9025 : overriding '/O"))
            {
                return;
            }

            if (line.Contains(this.ImageTag == "msvc" ? " error C" : ": error: "))
            {
                this.LogError(line);
            }
            else
            {
                this.LogWarning(line);
            }
        }

        private void OutputDataReceived(object sender, ProcessDataReceivedEventArgs args)
        {
            if (this.UseNinja)
            {
                this.NinjaOutputDataReceived(sender, args);
            }
            else
            {
                this.MakeOutputDataReceived(sender, args);
            }
        }

        private void ErrorDataReceived(object sender, ProcessDataReceivedEventArgs args)
        {
            if (this.UseNinja)
            {
                this.NinjaErrorDataReceived(sender, args);
            }
            else
            {
                this.MakeErrorDataReceived(sender, args);
            }
        }
    }
}
