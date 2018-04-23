using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.IO;

namespace Inedo.Extensions.DFHack.Operations
{
    [DisplayName("[DFHack] Configure with CMake")]
    [Description("Prepares a build of DFHack to be used with the DFHack::Make operation.")]
    [ScriptNamespace("DFHack", PreferUnqualified = false)]
    [ScriptAlias("Configure")]
    [AppliesTo(InedoProduct.BuildMaster)]
    public sealed class ConfigureOperation : BuildEnvOperationBase
    {
        public enum BuildArchitecture
        {
            i386,
            x86_64
        }

        public enum BuildOperatingSystem
        {
            Windows,
            Linux,
            MacOSX
        }

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

        [Category("Directories")]
        [DisplayName("Source path")]
        [ScriptAlias("SourcePath")]
        [DefaultValue("../src")]
        public string SourcePath { get; set; }

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
            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();

            var bits = AH.Switch<BuildArchitecture, int>(this.Architecture)
                .Case(BuildArchitecture.i386, 32)
                .Case(BuildArchitecture.x86_64, 64)
                .End();

            var escapeArg = this.OperatingSystem == BuildOperatingSystem.Windows ? (Func<string, string>)Utils.EscapeWindowsArg : Utils.EscapeLinuxArg;
            RemoteProcessStartInfo cmakeStartInfo;

            if (this.OperatingSystem == BuildOperatingSystem.Windows)
            {
                cmakeStartInfo = new RemoteProcessStartInfo
                {
                    FileName = @"C:\ProgramData\Chocolatey\bin\cmake.exe",
                    Arguments = $"-DDFHACK_BUILD_ARCH={bits} -DCMAKE_BUILD_TYPE={this.BuildType}" + (bits == 32 ? " -G\"Visual Studio 14\"" : " -G\"Visual Studio 14 Win64\" -T v140_xp"),
                    WorkingDirectory = context.WorkingDirectory
                };
            }
            else
            {
                cmakeStartInfo = new RemoteProcessStartInfo
                {
                    FileName = "dfhack-configure",
                    Arguments = $"{this.OperatingSystem.ToString().ToLowerInvariant()} {bits} {this.BuildType}",
                    WorkingDirectory = context.WorkingDirectory
                };
            }

            cmakeStartInfo.Arguments += $" {escapeArg(this.SourcePath)} -DCMAKE_INSTALL_PREFIX={escapeArg(this.InstallPrefix)} -DBUILD_SUPPORTED={(this.IncludeSupported ? 1 : 0)} -DBUILD_DEVEL=0 -DBUILD_DEV_PLUGINS=0 -DBUILD_DOCS={(this.IncludeDocumentation ? 1 : 0)} -DBUILD_STONESENSE={(this.IncludeStonesense ? 1 : 0)}";

            await fileOps.CreateDirectoryAsync(context.WorkingDirectory);

            if (this.OperatingSystem != BuildOperatingSystem.Windows)
            {
                await this.LogAndWrapCommandAsync(context, cmakeStartInfo);
            }
            else
            {
                this.LogDebug($"Running in directory: {cmakeStartInfo.WorkingDirectory}");
                this.LogDebug($"Executing command: {cmakeStartInfo.FileName} {cmakeStartInfo.Arguments}");
            }

            using (var cmake = execOps.CreateProcess(cmakeStartInfo))
            {
                cmake.OutputDataReceived += (s, e) =>
                {
                    this.LogInformation(e.Data);
                };

                bool isError = false;
                cmake.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data.StartsWith("* "))
                    {
                        this.LogDebug(e.Data);
                        isError = false;
                        return;
                    }

                    if (e.Data.Contains("Error"))
                    {
                        isError = true;
                    }
                    else if (string.IsNullOrWhiteSpace(e.Data))
                    {
                        isError = false;
                    }

                    if (isError)
                    {
                        this.LogError(e.Data);
                    }
                    else
                    {
                        this.LogWarning(e.Data);
                    }
                };

                cmake.Start();
                await cmake.WaitAsync(context.CancellationToken);

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

            if (this.OperatingSystem == BuildOperatingSystem.Windows)
            {
                var projects = await fileOps.GetFileSystemInfosAsync(context.WorkingDirectory, new MaskingContext(new[] { "**/*.vcxproj" }, new string[0]));

                if (projects.Count != 0)
                {
                    int i = 0;
                    this.progress = new OperationProgress(0, "replacing PDB types in Visual C++ projects");

                    foreach (var project in projects)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();

                        var text = await fileOps.ReadAllTextAsync(project.FullName);
                        text = text.Replace("<DebugInformationFormat>ProgramDatabase</DebugInformationFormat>", "<DebugInformationFormat>OldStyle</DebugInformationFormat>");
                        await fileOps.WriteAllTextAsync(project.FullName, text);

                        i++;
                        this.progress = new OperationProgress(i / projects.Count, "replacing PDB types in Visual C++ projects");
                    }
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            bool validOS = Enum.TryParse(config[nameof(OperatingSystem)], out BuildOperatingSystem os);
            bool validArch = Enum.TryParse(config[nameof(Architecture)], out BuildArchitecture arch);
            bool validGCC = Enum.TryParse(config[nameof(GCC)], out BuildGCC gcc);
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
                ), os == BuildOperatingSystem.Windows ? null : AH.Switch<BuildGCC, string>(validGCC ? gcc : BuildGCC.Latest)
                .Case(BuildGCC.Latest, " (latest GCC)")
                .Case(BuildGCC.GCC48, " (gcc 4.8)")
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
