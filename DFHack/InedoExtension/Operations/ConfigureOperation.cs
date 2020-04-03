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
    [DisplayName("[DFHack] Configure with CMake")]
    [Description("Prepares a build of DFHack to be used with the DFHack::Make operation.")]
    [ScriptAlias("Configure")]
    [AppliesTo(InedoProduct.BuildMaster)]
    public sealed class ConfigureOperation : BuildEnvOperationBase
    {
        public enum BuildTypeName
        {
            Release,
            RelWithDebInfo
        }

        [Required]
        [DisplayName("Operating system")]
        [ScriptAlias("OperatingSystem")]
        public BuildOperatingSystem OperatingSystem { get; set; }

        [Required]
        [DisplayName("Architecture")]
        [ScriptAlias("Architecture")]
        public BuildArchitecture Architecture { get; set; }

        [Required]
        [DisplayName("Build type")]
        [ScriptAlias("BuildType")]
        public BuildTypeName BuildType { get; set; }

        [DisplayName("Additional arguments")]
        [ScriptAlias("AdditionalArgs")]
        public IEnumerable<string> AdditionalArgs { get; set; }

        [DisplayName("Use Ninja instead of Make")]
        [ScriptAlias("UseNinja")]
        public bool UseNinja { get; set; }

        [Category("Directories")]
        [DisplayName("Source path")]
        [ScriptAlias("SourcePath")]
        [DefaultValue("../src")]
        public string SourcePath { get; set; } = "../src";

        [Required]
        [Category("Directories")]
        [DisplayName("Install prefix")]
        [ScriptAlias("InstallPrefix")]
        public string InstallPrefix { get; set; }

        [Category("Included components")]
        [DisplayName("Official plugins")]
        [ScriptAlias("IncludeSupported")]
        [DefaultValue(true)]
        public bool IncludeSupported { get; set; } = true;

        [Category("Included components")]
        [DisplayName("Documentation")]
        [ScriptAlias("IncludeDocumentation")]
        [DefaultValue(false)]
        public bool IncludeDocumentation { get; set; }

        [Category("Included components")]
        [DisplayName("Stonesense")]
        [ScriptAlias("IncludeStonesense")]
        [DefaultValue(false)]
        public bool IncludeStonesense { get; set; }

        private volatile OperationProgress progress = null;
        public override OperationProgress GetProgress() => this.progress;

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var execOps = await context.Agent.GetServiceAsync<IRemoteProcessExecuter>();
            var fileOps = await context.Agent.GetServiceAsync<ILinuxFileOperationsExecuter>();

            var bits = AH.Switch<BuildArchitecture, int>(this.Architecture)
                .Case(BuildArchitecture.i386, 32)
                .Case(BuildArchitecture.x86_64, 64)
                .End();

            await fileOps.CreateDirectoryAsync(context.WorkingDirectory);

            var cmakeStartInfo = new RemoteProcessStartInfo
            {
                FileName = "dfhack-configure",
                Arguments = $"{this.OperatingSystem.ToString().ToLowerInvariant()} {bits} {this.BuildType} {this.SourcePath.EscapeLinuxArg()} -DCMAKE_INSTALL_PREFIX={this.InstallPrefix.EscapeLinuxArg()} -DBUILD_SUPPORTED={(this.IncludeSupported ? 1 : 0)} -DBUILD_DEVEL=0 -DBUILD_DEV_PLUGINS=0 -DBUILD_DOCS={(this.IncludeDocumentation ? 1 : 0)} -DBUILD_STONESENSE={(this.IncludeStonesense ? 1 : 0)}{string.Join("", this.AdditionalArgs.Select(a => " " + a.EscapeLinuxArg()))}",
                WorkingDirectory = context.WorkingDirectory
            };

            if (this.UseNinja)
            {
                cmakeStartInfo.EnvironmentVariables["DFHACK_USE_NINJA"] = "1";
            }

            var cidfile = await cmakeStartInfo.WrapInBuildEnvAsync(context, this.BuildEnv + ":" + this.ImageTag, this.TrustedBuild);

            this.LogDebug($"Running in directory: {cmakeStartInfo.WorkingDirectory}");
            this.LogDebug($"Executing command: {cmakeStartInfo.FileName} {cmakeStartInfo.Arguments}");

            using (var cmake = execOps.CreateProcess(cmakeStartInfo))
            {
                cmake.OutputDataReceived += (s, e) =>
                {
                    this.LogInformation(e.Data);
                };

                bool isError = false;
                cmake.ErrorDataReceived += (s, e) =>
                {
                    var line = this.RemoveLogRubbish(e);
                    if (line == null)
                    {
                        return;
                    }

                    if (line.StartsWith("* "))
                    {
                        this.LogDebug(line);
                        isError = false;
                        return;
                    }

                    if (e.Data.Contains("Error"))
                    {
                        isError = true;
                    }
                    else if (string.IsNullOrWhiteSpace(line))
                    {
                        isError = false;
                    }

                    if (isError)
                    {
                        this.LogError(line);
                    }
                    else
                    {
                        this.LogWarning(line);
                    }
                };

                cmake.Start();
                using (ManageContainerIDFile(context, cidfile))
                {
                    await cmake.WaitAsync(context.CancellationToken);
                }

                if (cmake.ExitCode == 0)
                {
                    this.LogDebug("cmake exited with code 0 (success)");
                }
                else if (cmake.ExitCode.HasValue)
                {
                    this.LogError($"cmake exited with code {cmake.ExitCode} (failure)");
                }
                else
                {
                    this.LogError("cmake exited with unknown code");
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            bool validOS = Enum.TryParse(config[nameof(OperatingSystem)], out BuildOperatingSystem os);
            bool validArch = Enum.TryParse(config[nameof(Architecture)], out BuildArchitecture arch);
            bool validType = Enum.TryParse(config[nameof(BuildType)], out BuildTypeName type);

            var description = new ExtendedRichDescription(new RichDescription("Configure DFHack"), new RichDescription());

            if (validType)
            {
                description.ShortDescription.AppendContent(" in ", new Hilite(
                    AH.Switch<BuildTypeName, string>(type)
                    .Case(BuildTypeName.Release, "release")
                    .Case(BuildTypeName.RelWithDebInfo, "debug")
                    .End()
                ), " mode");
            }

            if (validOS)
            {
                description.ShortDescription.AppendContent(" for ");
                if (validArch)
                {
                    description.ShortDescription.AppendContent(new Hilite(
                        AH.Switch<BuildArchitecture, string>(arch)
                        .Case(BuildArchitecture.i386, "32-bit")
                        .Case(BuildArchitecture.x86_64, "64-bit")
                        .End()
                    ), " ");
                }
                description.ShortDescription.AppendContent(new Hilite(
                    AH.Switch<BuildOperatingSystem, string>(os)
                    .Case(BuildOperatingSystem.Windows, "Windows")
                    .Case(BuildOperatingSystem.Linux, "Linux")
                    .Case(BuildOperatingSystem.MacOSX, "Mac OS X")
                    .End()
                ), AH.Switch<string, string>(config[nameof(ImageTag)])
                    .Case("latest", " (latest GCC)")
                    .Case("gcc-4.8", " (GCC 4.8)")
                    .Default((string)null)
                    .End());
            }

            var with = " with ";
            if (string.Equals(config[nameof(IncludeSupported)], "false", StringComparison.OrdinalIgnoreCase))
            {
                description.LongDescription.AppendContent(" without ", new Hilite("official plugins"));
                with = ", with ";
            }
            if (string.Equals(config[nameof(IncludeDocumentation)], "true", StringComparison.OrdinalIgnoreCase))
            {
                description.LongDescription.AppendContent(with, new Hilite("documentation"));
                with = " and ";
            }
            if (string.Equals(config[nameof(IncludeStonesense)], "true", StringComparison.OrdinalIgnoreCase))
            {
                description.LongDescription.AppendContent(with, new Hilite("stonesense"));
            }

            return description;
        }
    }
}
