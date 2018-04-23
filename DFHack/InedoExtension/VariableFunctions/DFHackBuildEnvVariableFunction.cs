﻿using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.Extensibility.VariableFunctions;
using System.Threading.Tasks;

namespace Inedo.Extensions.DFHack.VariableFunctions
{
    [ScriptAlias(Name)]
    [ExtensionConfigurationVariable(Required = true)]
    public sealed class DFHackBuildEnvVariableFunction : ScalarVariableFunction
    {
        public const string Name = "DFHackBuildEnv";

        protected override object EvaluateScalar(IVariableFunctionContext context) => string.Empty;

        public static async Task<string> GetAsync(IOperationExecutionContext context) => (await context.ExpandVariablesAsync("$" + Name)).AsString();
    }
}