using System.ComponentModel;
using System.Threading.Tasks;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.Extensions.DFHack.VariableFunctions;

namespace Inedo.Extensions.DFHack.Operations
{
    public abstract class BuildEnvOperationBase : ExecuteOperation
    {
        public enum BuildGCC
        {
            Latest,
            GCC48
        }

        [DisplayName("Build-env image")]
        [ScriptAlias("BuildEnv")]
        [DefaultValue("$" + DFHackBuildEnvVariableFunction.Name)]
        public string BuildEnv { get; set; } = "$" + DFHackBuildEnvVariableFunction.Name;

        [DisplayName("GCC version")]
        [ScriptAlias("GCC")]
        [DefaultValue(BuildGCC.Latest)]
        public BuildGCC GCC { get; set; } = BuildGCC.Latest;

        [DisplayName("Build is trusted")]
        [ScriptAlias("Trusted")]
        [Description("Untrusted builds do not share caches with other executions.")]
        [DefaultValue(true)]
        public bool TrustedBuild { get; set; } = true;

        protected async Task LogAndWrapCommandAsync(IOperationExecutionContext context, RemoteProcessStartInfo info)
        {
            this.LogDebug($"Running in directory: {info.WorkingDirectory}");
            this.LogDebug($"Executing command: {info.FileName} {info.Arguments}");

            await info.WrapInBuildEnvAsync(context, this.BuildEnv + ":" + AH.Switch<BuildGCC, string>(this.GCC).Case(BuildGCC.Latest, "latest").Case(BuildGCC.GCC48, "gcc-4.8").End(), this.TrustedBuild);
            this.LogDebug($"Full build-env command line: {info.FileName} {info.Arguments}");
        }
    }
}
