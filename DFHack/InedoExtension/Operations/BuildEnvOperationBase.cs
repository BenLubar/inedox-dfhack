﻿using System.ComponentModel;
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
    public abstract class BuildEnvOperationBase : ExecuteOperation
    {
        public enum BuildOperatingSystem
        {
            Windows,
            Linux,
            MacOSX
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

        protected async Task LogAndWrapCommandAsync(IOperationExecutionContext context, RemoteProcessStartInfo info)
        {
            this.LogDebug($"Running in directory: {info.WorkingDirectory}");
            this.LogDebug($"Executing command: {info.FileName} {info.Arguments}");

            await info.WrapInBuildEnvAsync(context, this.BuildEnv + ":" + this.ImageTag, this.TrustedBuild);
            this.LogDebug($"Full build-env command line: {info.FileName} {info.Arguments}");
        }
    }
}
