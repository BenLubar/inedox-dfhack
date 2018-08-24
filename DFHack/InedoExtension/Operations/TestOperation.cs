using System.ComponentModel;
using System.Threading.Tasks;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;

namespace Inedo.Extensions.DFHack.Operations
{
    [DisplayName("[DFHack] Run Tests")]
    [Description("Runs DFHack test cases.")]
    [ScriptAlias("Test")]
    [AppliesTo(InedoProduct.BuildMaster)]
    public sealed class TestOperation : BuildEnvOperationBase
    {
        [Required]
        [DisplayName("Operating system")]
        [ScriptAlias("OperatingSystem")]
        public BuildOperatingSystem OperatingSystem { get; set; }

        [Required]
        [DisplayName("Architecture")]
        [ScriptAlias("Architecture")]
        public BuildArchitecture Architecture { get; set; }

        [Required]
        [DisplayName("DFHack command")]
        [ScriptAlias("Command")]
        [PlaceholderText("eg. test/main")]
        public string Command { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var execOps = await context.Agent.GetServiceAsync<IRemoteProcessExecuter>();

            var bits = AH.Switch<BuildArchitecture, int>(this.Architecture)
                .Case(BuildArchitecture.i386, 32)
                .Case(BuildArchitecture.x86_64, 64)
                .End();

            var testStartInfo = new RemoteProcessStartInfo
            {
                FileName = "dfhack-test",
                Arguments = $"{this.OperatingSystem.ToString().ToLowerInvariant()} {bits} {this.Command.EscapeLinuxArg()}",
                WorkingDirectory = context.WorkingDirectory
            };

            await this.LogAndWrapCommandAsync(context, testStartInfo, false, false);

            using (var test = execOps.CreateProcess(testStartInfo))
            {
                test.OutputDataReceived += (s, e) =>
                {
                    string text = e.Data;
                    if (this.ImageTag == "msvc")
                    {
                        text = e.Data.TrimEnd();
                        if (text == @"wine: cannot find L""C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\mscorsvw.exe""")
                        {
                            return;
                        }
                    }

                    this.LogInformation(text);
                };

                test.ErrorDataReceived += (s, e) =>
                {
                    string text = e.Data;
                    if (this.ImageTag == "msvc")
                    {
                        text = e.Data.TrimEnd();
                        if (text == @"wine: cannot find L""C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\mscorsvw.exe""")
                        {
                            return;
                        }
                    }

                    this.LogWarning(text);
                };

                test.Start();
                await test.WaitAsync(context.CancellationToken);

                if (test.ExitCode != 0)
                {
                    this.LogError("Tests failed!");
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription("Run DFHack test suite using ", new Hilite(AH.CoalesceString(config[nameof(Command)], "unknown command"))),
                new RichDescription("for ", new Hilite(AH.CoalesceString(config[nameof(OperatingSystem)], "unknown argument")), " (", new Hilite(AH.CoalesceString(config[nameof(Architecture)], "unknown architecture")), ")")
            );
        }
    }
}
