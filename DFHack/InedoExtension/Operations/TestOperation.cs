using System;
using System.Collections.Generic;
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
                Arguments = $"{this.OperatingSystem.ToString().ToLowerInvariant()} {bits}",
                WorkingDirectory = context.WorkingDirectory
            };

            await this.LogAndWrapCommandAsync(context, testStartInfo, false, false);

            var testLines = new List<string>();
            var recorder = await context.TryGetServiceAsync<IUnitTestRecorder>();
            Task recorderTask = InedoLib.NullTask;

            using (var test = execOps.CreateProcess(testStartInfo))
            {
                var lastTime = DateTimeOffset.Now;
                string currentFile = null;
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

                    testLines.Add(text);

                    if (text == "Running tests")
                    {
                        testLines.Clear();
                        lastTime = DateTimeOffset.Now;
                    }

                    if (text.StartsWith("Running file: "))
                    {
                        currentFile = text.Substring("Running file: ".Length);
                        if (currentFile.StartsWith("test/"))
                        {
                            currentFile = currentFile.Substring("test/".Length);
                        }
                        if (currentFile.EndsWith(".lua"))
                        {
                            currentFile = currentFile.Substring(0, currentFile.Length - ".lua".Length);
                        }
                        testLines.Clear();
                        lastTime = DateTimeOffset.Now;
                    }

                    if (text.StartsWith("warning: ") || text.StartsWith("WARN: "))
                    {
                        this.LogWarning(text);
                        return;
                    }

                    if (text.StartsWith("test passed: "))
                    {
                        var testName = text.Substring("test passed: ".Length);

                        recordUnitTest(currentFile, testName, UnitTestStatus.Passed, ref lastTime);
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

                    testLines.Add(text);

                    if (text.StartsWith("Plugin ") && text.Contains(" is missing required globals: "))
                    {
                        this.LogWarning(text);
                        return;
                    }

                    if (text.StartsWith("Error when running file: "))
                    {
                        recordUnitTest(currentFile, "(load file)", UnitTestStatus.Failed, ref lastTime);
                    }

                    if (text.StartsWith("test failed: "))
                    {
                        var testName = text.Substring("test failed: ".Length);

                        recordUnitTest(currentFile, testName, UnitTestStatus.Failed, ref lastTime);
                    }

                    if (text.StartsWith("test errored: "))
                    {
                        var testName = text.Substring("test errored: ".Length).Split(new[] { ':' }, 2)[0];

                        recordUnitTest(currentFile, testName, UnitTestStatus.Failed, ref lastTime);
                    }

                    this.LogError(text);
                };

                test.Start();
                await test.WaitAsync(context.CancellationToken);
                await recorderTask;

                if (test.ExitCode != 0)
                {
                    this.LogError("Tests failed!");
                }

                void recordUnitTest(string groupName, string testName, UnitTestStatus testStatus, ref DateTimeOffset now)
                {
                    var testResult = string.Join("\n", testLines);
                    testLines.Clear();

                    var prevRecorderTask = recorderTask;

                    var endTime = DateTimeOffset.Now;
                    var startTime = now;
                    now = endTime;

                    recorderTask = Task.Run(async () =>
                    {
                        await prevRecorderTask;
                        await recorder.RecordUnitTestAsync(groupName, testName, testStatus, testResult, startTime, endTime);
                    });
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription("Run DFHack test suite"),
                new RichDescription("for ", new Hilite(AH.CoalesceString(config[nameof(OperatingSystem)], "unknown operating system")),
                    " (", new Hilite(AH.CoalesceString(config[nameof(Architecture)], "unknown architecture")), ")")
            );
        }
    }
}
