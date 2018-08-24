using System.ComponentModel;
using System.Threading.Tasks;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.Extensions.DFHack.SuggestionProviders;
using Inedo.Extensions.DFHack.VariableFunctions;
using Inedo.Web;

namespace Inedo.Extensions.DFHack.Operations
{
    [ScriptNamespace("DFHack")]
    [Tag("dfhack")]
    [AppliesTo(InedoProduct.BuildMaster)]
    public abstract class BuildEnvOperationBase : ExecuteOperation
    {
        public enum BuildOperatingSystem
        {
            Windows,
            Linux,
            MacOSX
        }

        public enum BuildArchitecture
        {
            i386,
            x86_64
        }

        [DisplayName("Build-env image")]
        [ScriptAlias("BuildEnv")]
        [DefaultValue("$" + DFHackBuildEnvVariableFunction.Name)]
        public string BuildEnv { get; set; } = "$" + DFHackBuildEnvVariableFunction.Name;

        [Required]
        [DisplayName("Image tag")]
        [ScriptAlias("Image")]
        [SuggestableValue(typeof(BuildEnvImageSuggestionProvider))]
        public string ImageTag { get; set; }

        [DisplayName("Build is trusted")]
        [ScriptAlias("Trusted")]
        [Description("Untrusted builds do not share caches with other executions.")]
        [DefaultValue(true)]
        public bool TrustedBuild { get; set; } = true;

        protected async Task LogAndWrapCommandAsync(IOperationExecutionContext context, RemoteProcessStartInfo info, bool allowNetwork = true, bool forceASLR = true)
        {
            this.LogDebug($"Running in directory: {info.WorkingDirectory}");
            this.LogDebug($"Executing command: {info.FileName} {info.Arguments}");

            await info.WrapInBuildEnvAsync(context, this.BuildEnv + ":" + this.ImageTag, this.TrustedBuild, allowNetwork, forceASLR);
            this.LogDebug($"Full build-env command line: {info.FileName} {info.Arguments}");
        }

        protected string RemoveLogRubbish(ProcessDataReceivedEventArgs e)
        {
            var line = e.Data.TrimEnd();
            if (this.ImageTag == "msvc" && line == @"wine: cannot find L""C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\mscorsvw.exe""")
            {
                return null;
            }
            if (this.ImageTag == "msvc" && line.StartsWith("cl : Command line warning D9025 : overriding '/O"))
            {
                return null;
            }

            return line;
        }
    }
}
