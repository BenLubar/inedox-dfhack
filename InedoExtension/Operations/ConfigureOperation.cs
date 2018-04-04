using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.IO;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Inedo.Extensions.DFHack.Operations
{
    [DisplayName("[DFHack] Configure with CMake")]
    [Description("Prepares a build of DFHack to be used with the DFHack::Make operation.")]
    [ScriptNamespace("DFHack", PreferUnqualified = false)]
    [ScriptAlias("Configure")]
    public sealed class ConfigureOperation : ExecuteOperation
    {
        public enum BuildArchitecture
        {
            [Description("32-bit")]
            i386,
            [Description("64-bit")]
            x86_64
        }

        public enum BuildOperatingSystem
        {
            Windows,
            Linux,
            [Description("Linux (gcc 4.8)")]
            Linux48,
            MacOSX
        }

        public enum BuildTypeName
        {
            Release,
            RelWithDebInfo,
            MacOSXNative
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

        [Category("Directories")]
        [DisplayName("Native directory")]
        [ScriptAlias("NativeDirectory")]
        public string NativeDirectory { get; set; }

        [Category("Included components")]
        [DisplayName("Official plugins")]
        [ScriptAlias("IncludeSupported")]
        [DefaultValue(true)]
        public bool IncludeSupported { get; set; }

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
            var cmakeStartInfo = new RemoteProcessStartInfo
            {
                FileName = "cmake",
                Arguments = $"{escapeArg(this.SourcePath)} -DDFHACK_BUILD_ARCH={bits} -DCMAKE_INSTALL_PREFIX={escapeArg(this.InstallPrefix)}",
                WorkingDirectory = context.WorkingDirectory
            };

            if (this.BuildType == BuildTypeName.MacOSXNative)
            {
                if (this.OperatingSystem != BuildOperatingSystem.MacOSX || this.Architecture != BuildArchitecture.x86_64 || !string.IsNullOrEmpty(this.NativeDirectory))
                {
                    throw new InvalidOperationException("MacOSXNative build type is only allowed for 64-bit MacOSX and NativeDirectory must not be specified.");
                }

                cmakeStartInfo.Arguments += " -DCMAKE_CXX_COMPILER=/usr/lib/ccache/g++ -DCMAKE_C_COMPILER=/usr/lib/ccache/gcc";
            }
            else
            {
                cmakeStartInfo.Arguments += $" -DCMAKE_BUILD_TYPE={this.BuildType} -DBUILD_SUPPORTED={(this.IncludeSupported ? 1 : 0)} -DBUILD_DEVEL=0 -DBUILD_DEV_PLUGINS=0 -DBUILD_DOCS={(this.IncludeDocumentation ? 1 : 0)} -DBUILD_STONESENSE={(this.IncludeStonesense ? 1 : 0)}";

                if (this.OperatingSystem == BuildOperatingSystem.Windows)
                {
                    cmakeStartInfo.FileName = @"C:\ProgramData\Chocolatey\bin\cmake.exe";
                    cmakeStartInfo.Arguments += bits == 32 ? " -G\"Visual Studio 14\"" : " -G\"Visual Studio 14 Win64\" -T v140_xp";
                }
                else if (this.OperatingSystem == BuildOperatingSystem.Linux48)
                {
                    cmakeStartInfo.Arguments += " -DCMAKE_C_COMPILER=gcc-4.8 -DCMAKE_CXX_COMPILER=g++-4.8";
                }
                else if (this.OperatingSystem == BuildOperatingSystem.MacOSX)
                {
                    cmakeStartInfo.Arguments += $" -DCMAKE_SYSTEM_NAME=Darwin -DDFHACK_NATIVE_BUILD_DIR={escapeArg(this.NativeDirectory)} -DCMAKE_CXX_COMPILER=/usr/lib/ccache/x86_64-apple-darwin14-g++ -DCMAKE_C_COMPILER=/usr/lib/ccache/x86_64-apple-darwin14-gcc -DCMAKE_FIND_ROOT_PATH=/osxcross/target/macports/pkgs/opt/local -DCMAKE_OSX_SYSROOT=/osxcross/target/SDK/MacOSX10.10.sdk";
                }
            }

            await fileOps.CreateDirectoryAsync(context.WorkingDirectory);

            this.LogDebug($"Running in directory: {context.WorkingDirectory}");
            this.LogDebug($"Running cmake with arguments: {cmakeStartInfo.Arguments}");

            if (this.OperatingSystem != BuildOperatingSystem.Windows)
            {
                await cmakeStartInfo.AddCCacheAsync(context);

                this.LogDebug($"Adjusted command for ccache: {cmakeStartInfo.FileName} {cmakeStartInfo.Arguments}");

                if (this.OperatingSystem == BuildOperatingSystem.MacOSX)
                {
                    await cmakeStartInfo.WrapInMacGCCAsync(context);

                    this.LogDebug($"Adjusted command for Mac build: {cmakeStartInfo.FileName} {cmakeStartInfo.Arguments}");
                }
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
            bool validType = Enum.TryParse(config[nameof(BuildType)], out BuildTypeName type);

            if (validType && type == BuildTypeName.MacOSXNative)
            {
                return new ExtendedRichDescription(new RichDescription("Configure native build tools for a Mac OS X DFHack build"));
            }

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
                    .Case(BuildOperatingSystem.Linux48, "Linux")
                    .Case(BuildOperatingSystem.MacOSX, "Mac OS X")
                    .End()
                ), AH.Switch<BuildOperatingSystem, string>(os)
                .Case(BuildOperatingSystem.Linux, " (latest GCC)")
                .Case(BuildOperatingSystem.Linux48, " (gcc 4.8)")
                .Default("")
                .End());
            }

            var with = " with ";
            if (string.Equals(config[nameof(IncludeSupported)], "false", StringComparison.OrdinalIgnoreCase))
            {
                description.LongDescription.AppendContent(" without ", new Hilite("official plugins"));
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
