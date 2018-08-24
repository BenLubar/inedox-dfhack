using System.Threading.Tasks;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.Extensibility.VariableFunctions;

namespace Inedo.Extensions.DFHack.VariableFunctions
{
    [ScriptAlias(Name)]
    [ExtensionConfigurationVariable(Required = true)]
    [AppliesTo(InedoProduct.BuildMaster)]
    public sealed class DFHackBuildEnvVariableFunction : ScalarVariableFunction
    {
        public const string Name = "DFHackBuildEnv";

        protected override object EvaluateScalar(IVariableFunctionContext context) => string.Empty;

        public static async Task<string> GetAsync(IOperationExecutionContext context) => (await context.ExpandVariablesAsync("$" + Name)).AsString();
    }
}
